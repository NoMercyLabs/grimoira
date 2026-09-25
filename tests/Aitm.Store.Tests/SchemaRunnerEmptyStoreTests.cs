using Aitm.Brain.Schema;
using Aitm.Docs.Schema;
using Aitm.Facts.Schema;
using Aitm.Graph.Schema;
using Aitm.Memory.Schema;
using Aitm.Store.Data;
using Aitm.Store.Schema;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Aitm.Store.Tests;

// RESTRUCTURE.md slice 3c: "an empty temp store gets exactly the pinned schema (reuse the parity
// oracle)". The parity oracle is WholeStoreSchemaDdlParityTests: it already proves SchemaRunner.Apply
// on the 6 providers below reproduces today's aitm.cs Init()+InitBrain() byte for byte. This file reuses
// that proven output as the expected shape, and checks the full SchemaRunner.Run path (backup, per-
// provider transaction, meta bookkeeping) against it, plus the migration-path rules that only apply
// once a real file (not a :memory: connection) and a runner (not a bare Apply) are involved.
public class SchemaRunnerEmptyStoreTests
{
    private static ISchemaProvider[] AllProviders() =>
    [
        new StoreSchema(), new FactsSchema(), new MemorySchema(), new DocsSchema(), new GraphSchema(), new BrainSchema(),
    ];

    [Fact]
    public void RunOnEmptyStoreProducesExactlyThePinnedSchema()
    {
        // The oracle: SchemaRunner.Apply on the same 6 providers, on a bare in-memory store — already
        // pinned against today's aitm.cs by WholeStoreSchemaDdlParityTests.
        using SqliteConnection oracle = new("Data Source=:memory:");
        oracle.Open();
        SchemaRunner.Apply(oracle, AllProviders());
        List<(string, string, string, string)> expected = SqliteMasterRows(oracle);

        string dbPath = Path.Combine(Path.GetTempPath(), $"aitm-store-tests-empty-{Guid.NewGuid():N}.db");
        string backupDir = Path.Combine(Path.GetTempPath(), $"aitm-store-tests-backups-{Guid.NewGuid():N}");
        try
        {
            using SqliteConnection connection = StoreConnection.Open(dbPath);
            SchemaRunResult result = SchemaRunner.Run(connection, AllProviders(), backupDir);

            Assert.True(result.Success, result.Error);
            Assert.Equal(AllProviders().Select(p => p.Name), result.AppliedProviders);
            Assert.Equal(expected, SqliteMasterRows(connection));
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

    [Fact]
    public void RunOnEmptyStoreKeepsUserVersionAt3()
    {
        // As in production: by the time the runner ever sees a store, the still-running old aitm.cs has
        // already stamped user_version to 3 on its own first open (aitm.cs:40-46). The runner must leave
        // that stamp alone (rule 3's ping-pong trap), even though it adds no rows of its own here beyond
        // the schema and its own step bookkeeping.
        string dbPath = Path.Combine(Path.GetTempPath(), $"aitm-store-tests-empty-v3-{Guid.NewGuid():N}.db");
        string backupDir = Path.Combine(Path.GetTempPath(), $"aitm-store-tests-backups-{Guid.NewGuid():N}");
        try
        {
            using (SqliteConnection connection = StoreConnection.Open(dbPath))
            {
                Exec(connection, "PRAGMA user_version=3");
                SchemaRunResult result = SchemaRunner.Run(connection, AllProviders(), backupDir);
                Assert.True(result.Success, result.Error);
                Assert.Equal(3L, ScalarLong(connection, "PRAGMA user_version"));
            }
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

    [Fact]
    public void EachProviderRecordsItsStepInMetaAndIsIdempotentOnASecondRun()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"aitm-store-tests-meta-{Guid.NewGuid():N}.db");
        string backupDir = Path.Combine(Path.GetTempPath(), $"aitm-store-tests-backups-{Guid.NewGuid():N}");
        try
        {
            using SqliteConnection connection = StoreConnection.Open(dbPath);

            SchemaRunResult first = SchemaRunner.Run(connection, AllProviders(), backupDir);
            Assert.True(first.Success, first.Error);
            Dictionary<string, string> metaAfterFirst = ReadMetaSchemaKeys(connection);
            foreach (ISchemaProvider provider in AllProviders())
                Assert.Equal("1", metaAfterFirst[$"schema:{provider.Name}"]);

            SchemaRunResult second = SchemaRunner.Run(connection, AllProviders(), backupDir);
            Assert.True(second.Success, second.Error);
            Dictionary<string, string> metaAfterSecond = ReadMetaSchemaKeys(connection);

            Assert.Equal(metaAfterFirst, metaAfterSecond);
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

    private static Dictionary<string, string> ReadMetaSchemaKeys(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT key, value FROM meta WHERE key LIKE 'schema:%'";
        using SqliteDataReader reader = command.ExecuteReader();
        Dictionary<string, string> rows = [];
        while (reader.Read()) rows[reader.GetString(0)] = reader.GetString(1);
        return rows;
    }

    private static void Exec(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static long ScalarLong(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static List<(string type, string name, string tblName, string sql)> SqliteMasterRows(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT type, name, tbl_name, sql FROM sqlite_master ORDER BY type, name";
        using SqliteDataReader reader = command.ExecuteReader();
        List<(string, string, string, string)> rows = [];
        while (reader.Read())
            rows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? "" : reader.GetString(3)));
        return rows;
    }
}
