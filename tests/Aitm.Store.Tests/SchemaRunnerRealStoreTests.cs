using Aitm.Brain.Schema;
using Aitm.Docs.Schema;
using Aitm.Facts.Schema;
using Aitm.Graph.Schema;
using Aitm.Memory.Schema;
using Aitm.Store.Data;
using Aitm.Store.Schema;
using Aitm.Store.Tests.Support;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Aitm.Store.Tests;

// RESTRUCTURE.md slice 3c / section 3.2 rule 3 ("new steps are additive and idempotent"): running the
// runner against a store today's aitm.cs already built must not touch its DDL, must not change any
// existing row, and must never bump user_version past 3 — the ping-pong trap an old binary or a live
// session could still hit while phase 2 runs.
public class SchemaRunnerRealStoreTests
{
    private static ISchemaProvider[] AllProviders() =>
    [
        new StoreSchema(), new FactsSchema(), new MemorySchema(), new DocsSchema(), new GraphSchema(), new BrainSchema(),
    ];

    [Fact]
    public void RunOnRealV3StoreCopyMakesNoDdlChanges()
    {
        string dbPath = CopyOfFixture();
        try
        {
            long schemaVersionBefore = ReadSchemaVersion(dbPath);
            List<(string, string, string, string)> masterBefore = ReadSqliteMaster(dbPath);

            using (SqliteConnection connection = StoreConnection.Open(dbPath))
            {
                SchemaRunResult result = SchemaRunner.Run(connection, AllProviders(), BackupDir());
                Assert.True(result.Success, result.Error);
            }
            SqliteConnection.ClearAllPools();

            long schemaVersionAfter = ReadSchemaVersion(dbPath);
            List<(string, string, string, string)> masterAfter = ReadSqliteMaster(dbPath);

            // No DDL statement created, dropped or altered anything: SQLite's own schema_version only
            // advances when the schema text actually changes, so an unchanged counter is proof no
            // `CREATE ... IF NOT EXISTS` did any work at all, on top of the row-for-row sqlite_master
            // comparison below.
            Assert.Equal(schemaVersionBefore, schemaVersionAfter);
            Assert.Equal(masterBefore, masterAfter);
        }
        finally
        {
            CleanupCopy(dbPath);
        }
    }

    [Fact]
    public void RunOnRealV3StoreCopyChangesNoExistingRow()
    {
        string dbPath = CopyOfFixture();
        try
        {
            Dictionary<string, string> factsBefore = ReadTable(dbPath, "facts", "k");
            Dictionary<string, string> mutationsBefore = ReadTable(dbPath, "mutations", "id");
            Dictionary<string, string> metaBefore = ReadTable(dbPath, "meta", "key");

            using (SqliteConnection connection = StoreConnection.Open(dbPath))
            {
                SchemaRunResult result = SchemaRunner.Run(connection, AllProviders(), BackupDir());
                Assert.True(result.Success, result.Error);
            }
            SqliteConnection.ClearAllPools();

            Dictionary<string, string> factsAfter = ReadTable(dbPath, "facts", "k");
            Dictionary<string, string> mutationsAfter = ReadTable(dbPath, "mutations", "id");
            Dictionary<string, string> metaAfter = ReadTable(dbPath, "meta", "key");

            // The fixture's data tables are untouched, row for row.
            Assert.Equal(factsBefore, factsAfter);
            Assert.Equal(mutationsBefore, mutationsAfter);

            // meta gains exactly the additive "schema:<project>" step markers (one per provider); every
            // row that already existed keeps the exact value it had.
            foreach ((string key, string value) in metaBefore) Assert.Equal(value, metaAfter[key]);
            IEnumerable<string> addedKeys = metaAfter.Keys.Except(metaBefore.Keys);
            Assert.Equal(AllProviders().Select(p => $"schema:{p.Name}").OrderBy(k => k), addedKeys.OrderBy(k => k));
        }
        finally
        {
            CleanupCopy(dbPath);
        }
    }

    [Fact]
    public void RunOnRealV3StoreCopyKeepsUserVersionAt3()
    {
        string dbPath = CopyOfFixture();
        try
        {
            Assert.Equal(3L, ReadUserVersion(dbPath));

            using (SqliteConnection connection = StoreConnection.Open(dbPath))
            {
                SchemaRunResult result = SchemaRunner.Run(connection, AllProviders(), BackupDir());
                Assert.True(result.Success, result.Error);
            }
            SqliteConnection.ClearAllPools();

            Assert.Equal(3L, ReadUserVersion(dbPath));
        }
        finally
        {
            CleanupCopy(dbPath);
        }
    }

    // --- fixture plumbing ---

    private static string CopyOfFixture()
    {
        using V3StoreFixture fixture = V3StoreFixture.Create();
        string copyPath = Path.Combine(Path.GetTempPath(), $"aitm-store-tests-v3copy-{Guid.NewGuid():N}.db");
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Copy(fixture.DbPath, copyPath);
        // The fixture instance is deleted immediately after the copy — only the copy is opened by the
        // runner, per the card: "opens through the runner", never the original.
        return copyPath;
    }

    private static void CleanupCopy(string dbPath)
    {
        SqliteConnection.ClearAllPools();
        foreach (string suffix in new[] { "", "-wal", "-shm" })
        {
            try { File.Delete(dbPath + suffix); } catch (IOException) { }
        }
    }

    private static string BackupDir() =>
        Path.Combine(Path.GetTempPath(), $"aitm-store-tests-backups-{Guid.NewGuid():N}");

    private static long ReadSchemaVersion(string dbPath) => ReadPragmaLong(dbPath, "PRAGMA schema_version");
    private static long ReadUserVersion(string dbPath) => ReadPragmaLong(dbPath, "PRAGMA user_version");

    private static long ReadPragmaLong(string dbPath, string pragma)
    {
        using SqliteConnection connection = new($"Data Source={dbPath}");
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = pragma;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static List<(string type, string name, string tblName, string sql)> ReadSqliteMaster(string dbPath)
    {
        using SqliteConnection connection = new($"Data Source={dbPath}");
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT type, name, tbl_name, sql FROM sqlite_master ORDER BY type, name";
        using SqliteDataReader reader = command.ExecuteReader();
        List<(string, string, string, string)> rows = [];
        while (reader.Read())
            rows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? "" : reader.GetString(3)));
        return rows;
    }

    private static Dictionary<string, string> ReadTable(string dbPath, string table, string keyColumn)
    {
        using SqliteConnection connection = new($"Data Source={dbPath}");
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT * FROM {table} ORDER BY {keyColumn}";
        using SqliteDataReader reader = command.ExecuteReader();
        Dictionary<string, string> rows = [];
        int keyOrdinal = reader.GetOrdinal(keyColumn);
        while (reader.Read())
        {
            string key = Convert.ToString(reader.GetValue(keyOrdinal)) ?? "";
            string[] values = [.. Enumerable.Range(0, reader.FieldCount).Select(i => reader.IsDBNull(i) ? "<null>" : Convert.ToString(reader.GetValue(i)) ?? "")];
            rows[key] = string.Join('\u0001', values);
        }
        return rows;
    }
}
