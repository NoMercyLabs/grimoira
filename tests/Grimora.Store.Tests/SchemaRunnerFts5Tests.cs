using Grimora.Facts.Schema;
using Grimora.Store.Data;
using Grimora.Store.Schema;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Grimora.Store.Tests;

// RESTRUCTURE.md slice 3c: "FTS5 works through the runner (insert + MATCH), replacing smoke.cs's FTS
// check." smoke.cs proved the bundled SQLite build has FTS5 at all, on its own ad hoc virtual table;
// this proves the same FTS5 build works on a virtual table the runner itself creates (facts_fts, from
// FactsSchema), which is what the product actually reads and writes.
public class SchemaRunnerFts5Tests
{
    [Fact]
    public void Fts5WorksThroughTheRunner()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"grimora-store-tests-fts5-{Guid.NewGuid():N}.db");
        string backupDir = Path.Combine(Path.GetTempPath(), $"grimora-store-tests-backups-{Guid.NewGuid():N}");
        try
        {
            using SqliteConnection connection = StoreConnection.Open(dbPath);
            SchemaRunResult result = SchemaRunner.Run(connection, [new StoreSchema(), new FactsSchema()], backupDir);
            Assert.True(result.Success, result.Error);

            Exec(connection,
                "INSERT INTO facts_fts(k,term,aliases,category,value,notes) VALUES " +
                "('fixture:media-url','media base url','[]','manual'," +
                "'https://raw.githubusercontent.com/NoMercy-Entertainment/nomercy-media/master','')");

            string? hit = Scalar(connection,
                "SELECT value FROM facts_fts WHERE facts_fts MATCH 'media url' ORDER BY bm25(facts_fts) LIMIT 1");

            Assert.Equal("https://raw.githubusercontent.com/NoMercy-Entertainment/nomercy-media/master", hit);
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

    private static void Exec(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static string? Scalar(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar());
    }
}
