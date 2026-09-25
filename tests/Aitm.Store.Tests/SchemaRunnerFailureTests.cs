using Aitm.Store.Data;
using Aitm.Store.Schema;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Aitm.Store.Tests;

// RESTRUCTURE.md slice 3c: "a failing provider step rolls back and leaves the store unchanged."
public class SchemaRunnerFailureTests
{
    private sealed class BrokenProvider : ISchemaProvider
    {
        public string Name => "Broken";
        public IReadOnlyList<string> Statements { get; } =
        [
            "CREATE TABLE IF NOT EXISTS broken_survivor(id INTEGER PRIMARY KEY);",
            "THIS IS NOT VALID SQL;",
        ];
    }

    [Fact]
    public void FailingProviderRollsBackAndLeavesTheStoreUnchanged()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"aitm-store-tests-failure-{Guid.NewGuid():N}.db");
        string backupDir = Path.Combine(Path.GetTempPath(), $"aitm-store-tests-backups-{Guid.NewGuid():N}");
        try
        {
            using SqliteConnection connection = StoreConnection.Open(dbPath);

            // Establish a known-good baseline first, exactly like every other slice-3c test.
            SchemaRunResult baseline = SchemaRunner.Run(connection, [new StoreSchema()], backupDir);
            Assert.True(baseline.Success, baseline.Error);

            List<(string, string, string)> masterBefore = SqliteMasterRows(connection);
            Dictionary<string, string> metaBefore = ReadMeta(connection);

            SchemaRunResult result = SchemaRunner.Run(connection, [new BrokenProvider()], backupDir);

            Assert.False(result.Success);
            Assert.NotNull(result.Error);
            Assert.Empty(result.AppliedProviders);

            List<(string, string, string)> masterAfter = SqliteMasterRows(connection);
            Dictionary<string, string> metaAfter = ReadMeta(connection);

            // Neither the first statement of the failing provider (broken_survivor) nor its meta step
            // marker survived: the whole provider rolled back as one unit, not statement by statement.
            Assert.Equal(masterBefore, masterAfter);
            Assert.Equal(metaBefore, metaAfter);
            Assert.DoesNotContain("schema:Broken", metaAfter.Keys);
            Assert.DoesNotContain(masterAfter, row => row.Item2 == "broken_survivor");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (string suffix in new[] { "", "-wal", "-shm" })
            {
                try { File.Delete(dbPath + suffix); } catch (IOException) { }
            }
        }
    }

    private static Dictionary<string, string> ReadMeta(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT key, value FROM meta";
        using SqliteDataReader reader = command.ExecuteReader();
        Dictionary<string, string> rows = [];
        while (reader.Read()) rows[reader.GetString(0)] = reader.GetString(1);
        return rows;
    }

    private static List<(string type, string name, string sql)> SqliteMasterRows(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT type, name, sql FROM sqlite_master ORDER BY type, name";
        using SqliteDataReader reader = command.ExecuteReader();
        List<(string, string, string)> rows = [];
        while (reader.Read())
            rows.Add((reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? "" : reader.GetString(2)));
        return rows;
    }
}
