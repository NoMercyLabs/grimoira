using System.Security.Cryptography;
using System.Text;
using Grimoira.Store.Data;
using Grimoira.Store.Schema;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Grimoira.Store.Tests;

// A pre-step backup is made only when a provider's statements changed since the last successful apply.
// Before this, every open of a store made one full VACUUM INTO copy per provider (7 copies of a 570 MB
// store on every server start), the integrity-check connection kept each copy open through the pool,
// and nothing ever pruned the copies.
public class SchemaRunnerBackupOnChangeTests
{
    private sealed class TestProvider(string name, params string[] statements) : ISchemaProvider
    {
        public string Name => name;
        public IReadOnlyList<string> Statements { get; } = statements;
    }

    private static ISchemaProvider ProviderA() =>
        new TestProvider("A", "CREATE TABLE IF NOT EXISTS a_one(id INTEGER PRIMARY KEY);");

    private static ISchemaProvider ProviderAChanged() =>
        new TestProvider("A",
            "CREATE TABLE IF NOT EXISTS a_one(id INTEGER PRIMARY KEY);",
            "CREATE TABLE IF NOT EXISTS a_two(id INTEGER PRIMARY KEY);");

    private static ISchemaProvider ProviderB() =>
        new TestProvider("B", "CREATE TABLE IF NOT EXISTS b_one(id INTEGER PRIMARY KEY);");

    private static string ExpectedHash(ISchemaProvider provider) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", provider.Statements)))).ToLowerInvariant();

    [Fact]
    public void SecondRunOnAnUnchangedStoreMakesNoBackup()
    {
        using TempStore store = new("unchanged");
        ISchemaProvider[] providers = [new StoreSchema(), ProviderA(), ProviderB()];

        SchemaRunResult first = SchemaRunner.Run(store.Connection, providers, store.BackupDir);
        Assert.True(first.Success, first.Error);
        Assert.Equal(3, store.BackupCount());
        foreach (ISchemaProvider provider in providers)
            Assert.Equal(ExpectedHash(provider), store.ReadMeta()[$"schema:{provider.Name}"]);

        SchemaRunResult second = SchemaRunner.Run(store.Connection, providers, store.BackupDir);
        Assert.True(second.Success, second.Error);
        Assert.Equal(3, store.BackupCount());
    }

    [Fact]
    public void AStoreWithTheOldMarkerValueGetsOneBackupPerProviderThenNone()
    {
        using TempStore store = new("legacy-marker");
        ISchemaProvider[] providers = [new StoreSchema(), ProviderA(), ProviderB()];
        Assert.True(SchemaRunner.Run(store.Connection, providers, store.BackupDir).Success);
        store.Exec("UPDATE meta SET value = '1' WHERE key LIKE 'schema:%'");
        store.DeleteAllBackups();

        SchemaRunResult migrating = SchemaRunner.Run(store.Connection, providers, store.BackupDir);
        Assert.True(migrating.Success, migrating.Error);
        Assert.Equal(3, store.BackupCount());
        foreach (ISchemaProvider provider in providers)
            Assert.Equal(ExpectedHash(provider), store.ReadMeta()[$"schema:{provider.Name}"]);

        SchemaRunResult settled = SchemaRunner.Run(store.Connection, providers, store.BackupDir);
        Assert.True(settled.Success, settled.Error);
        Assert.Equal(3, store.BackupCount());
    }

    [Fact]
    public void AChangedProviderGetsABackupForThatProviderOnly()
    {
        using TempStore store = new("changed-provider");
        Assert.True(SchemaRunner.Run(store.Connection, [new StoreSchema(), ProviderA(), ProviderB()], store.BackupDir).Success);
        store.DeleteAllBackups();

        SchemaRunResult result = SchemaRunner.Run(store.Connection, [new StoreSchema(), ProviderAChanged(), ProviderB()], store.BackupDir);

        Assert.True(result.Success, result.Error);
        string backup = Assert.Single(store.Backups());
        Assert.StartsWith("pre-A-", Path.GetFileName(backup));
        Assert.Equal(ExpectedHash(ProviderAChanged()), store.ReadMeta()["schema:A"]);
    }

    [Fact]
    public void TheBackupFileIsReleasedRightAfterRun()
    {
        // The integrity check opened the copy through the pooled connection path, so the pool kept the
        // file open after Dispose and the running server held every backup until it exited.
        using TempStore store = new("handle");
        Assert.True(SchemaRunner.Run(store.Connection, [new StoreSchema()], store.BackupDir).Success);
        string backup = Assert.Single(store.Backups());

        File.Delete(backup);

        Assert.False(File.Exists(backup));
    }

    [Fact]
    public void OnlyTheNewestThreeBackupsAreKept()
    {
        using TempStore store = new("prune");
        Assert.True(SchemaRunner.Run(store.Connection, [new StoreSchema()], store.BackupDir).Success);
        for (int i = 0; i < 5; i++)
        {
            ISchemaProvider changing = new TestProvider("C", $"CREATE TABLE IF NOT EXISTS c_{i}(id INTEGER PRIMARY KEY);");
            SchemaRunResult result = SchemaRunner.Run(store.Connection, [changing], store.BackupDir);
            Assert.True(result.Success, result.Error);
        }

        Assert.True(store.BackupCount() <= 3, $"{store.BackupCount()} backups remain");
        Assert.True(File.Exists(store.DbPath));
    }

    [Fact]
    public void PruneKeepsTheNewestThreeByWriteTimeNotByName()
    {
        // Run order "Zeta" then "Alpha": by name Zeta sorts after Alpha, so a name-ordered prune would keep
        // three Zeta copies and throw away the newest Alpha one.
        using TempStore store = new("prune-order");
        Assert.True(SchemaRunner.Run(store.Connection, [new StoreSchema()], store.BackupDir).Success);
        string[] afterPreviousRun = [];
        string[] afterLastRun = [];
        for (int i = 0; i < 5; i++)
        {
            ISchemaProvider zeta = new TestProvider("Zeta", $"CREATE TABLE IF NOT EXISTS z_{i}(id INTEGER PRIMARY KEY);");
            ISchemaProvider alpha = new TestProvider("Alpha", $"CREATE TABLE IF NOT EXISTS a_{i}(id INTEGER PRIMARY KEY);");
            afterPreviousRun = afterLastRun;
            SchemaRunResult result = SchemaRunner.Run(store.Connection, [zeta, alpha], store.BackupDir);
            Assert.True(result.Success, result.Error);
            afterLastRun = store.Backups();
            Thread.Sleep(20);
        }

        string[] kept = store.Backups();
        Assert.Equal(3, kept.Length);
        string[] madeByLastRun = [.. afterLastRun.Except(afterPreviousRun)];
        Assert.Equal(2, madeByLastRun.Length);
        Assert.Contains(madeByLastRun, path => Path.GetFileName(path).StartsWith("pre-Zeta-", StringComparison.Ordinal));
        Assert.Contains(madeByLastRun, path => Path.GetFileName(path).StartsWith("pre-Alpha-", StringComparison.Ordinal));
        foreach (string path in madeByLastRun) Assert.Contains(path, kept);
        string third = Assert.Single(kept.Except(madeByLastRun));
        Assert.Contains(third, afterPreviousRun);
    }

    [Fact]
    public void AFailingProviderKeepsItsBackupFile()
    {
        using TempStore store = new("failure-keeps-backup");
        Assert.True(SchemaRunner.Run(store.Connection, [new StoreSchema()], store.BackupDir).Success);
        for (int i = 0; i < 3; i++)
        {
            ISchemaProvider zeta = new TestProvider("Zeta", $"CREATE TABLE IF NOT EXISTS z_{i}(id INTEGER PRIMARY KEY);");
            Assert.True(SchemaRunner.Run(store.Connection, [zeta], store.BackupDir).Success);
        }
        ISchemaProvider broken = new TestProvider("Broken", "THIS IS NOT VALID SQL;");

        SchemaRunResult result = SchemaRunner.Run(store.Connection, [broken], store.BackupDir);

        Assert.False(result.Success);
        Assert.NotNull(result.BackupPath);
        Assert.True(File.Exists(result.BackupPath), $"backup for the failing provider is gone: {result.BackupPath}");
    }

    private sealed class TempStore : IDisposable
    {
        public string DbPath { get; }
        public string BackupDir { get; }
        public SqliteConnection Connection { get; }

        public TempStore(string tag)
        {
            DbPath = Path.Combine(Path.GetTempPath(), $"grimoira-store-tests-{tag}-{Guid.NewGuid():N}.db");
            BackupDir = Path.Combine(Path.GetTempPath(), $"grimoira-store-tests-backups-{Guid.NewGuid():N}");
            Connection = StoreConnection.Open(DbPath);
        }

        public string[] Backups() => Directory.Exists(BackupDir) ? Directory.GetFiles(BackupDir, "pre-*.db") : [];

        public int BackupCount() => Backups().Length;

        public void DeleteAllBackups()
        {
            SqliteConnection.ClearAllPools();
            foreach (string file in Backups()) File.Delete(file);
        }

        public void Exec(string sql)
        {
            using SqliteCommand command = Connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

        public Dictionary<string, string> ReadMeta()
        {
            using SqliteCommand command = Connection.CreateCommand();
            command.CommandText = "SELECT key, value FROM meta WHERE key LIKE 'schema:%'";
            using SqliteDataReader reader = command.ExecuteReader();
            Dictionary<string, string> rows = [];
            while (reader.Read()) rows[reader.GetString(0)] = reader.GetString(1);
            return rows;
        }

        public void Dispose()
        {
            Connection.Dispose();
            SqliteConnection.ClearAllPools();
            foreach (string suffix in new[] { "", "-wal", "-shm" })
            {
                try { File.Delete(DbPath + suffix); } catch (IOException) { }
            }
            foreach (string file in Backups())
            {
                try { File.Delete(file); } catch (IOException) { }
            }
            try { if (Directory.Exists(BackupDir)) Directory.Delete(BackupDir); } catch (IOException) { }
        }
    }
}
