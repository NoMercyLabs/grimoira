using Grimora.Brain.Tools;
using Grimora.Store.Data;
using Grimora.TestSupport;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Grimora.Brain.Tests;

public class BrainTidyToolTests
{
    // Oracle: today's grimora.cs BrainTidy (grimora.cs:1899-1906), reached via CLI `brain tidy`.

    [Fact]
    public void MatchesTodaysCliOutputAndBackfillsSchemeFromKindOnSchemelessNodesOnly()
    {
        string oldInstance = GrimoraCliRunner.NewTestInstance("brain-tidy-old");
        string newInstance = GrimoraCliRunner.NewTestInstance("brain-tidy-new");
        try
        {
            GrimoraCliRunner.Run($"init --instance {oldInstance}");
            string oldDb = GrimoraCliRunner.InstanceDbPath(oldInstance);
            BrainTestFixtures.InsertNode(oldDb, "tidy:bare", "concept", "no scheme", "");
            SetScheme(oldDb, "tidy:has", "concept", "has scheme", "custom");
            (string stdout, int exitCode) = GrimoraCliRunner.Run($"brain tidy --instance {oldInstance}");
            Assert.Equal(0, exitCode);
            string expected = stdout.Trim();

            GrimoraCliRunner.Run($"init --instance {newInstance}");
            string newDb = GrimoraCliRunner.InstanceDbPath(newInstance);
            BrainTestFixtures.InsertNode(newDb, "tidy:bare", "concept", "no scheme", "");
            SetScheme(newDb, "tidy:has", "concept", "has scheme", "custom");

            string actual;
            using (SqliteConnection connection = StoreConnection.Open(newDb))
            {
                actual = new BrainTidyTool().Execute(connection, GrimoraCliRunner.InstanceDir(newInstance)).Trim();
            }

            Assert.Equal(expected, actual);
            Assert.Equal("tidy: backfilled scheme=kind on 1 node(s).", actual);

            using SqliteConnection check = StoreConnection.Open(newDb);
            using SqliteCommand bareScheme = check.CreateCommand();
            bareScheme.CommandText = "SELECT scheme FROM node_now WHERE k='tidy:bare'";
            Assert.Equal("concept", (string)bareScheme.ExecuteScalar()!);

            using SqliteCommand hasScheme = check.CreateCommand();
            hasScheme.CommandText = "SELECT scheme FROM node_now WHERE k='tidy:has'";
            Assert.Equal("custom", (string)hasScheme.ExecuteScalar()!);
        }
        finally
        {
            GrimoraCliRunner.DeleteInstance(oldInstance);
            GrimoraCliRunner.DeleteInstance(newInstance);
        }
    }

    [Fact]
    public void MakesABackupBeforeTidying()
    {
        string instance = GrimoraCliRunner.NewTestInstance("brain-tidy-backup");
        string root = GrimoraCliRunner.InstanceDir(instance);
        try
        {
            GrimoraCliRunner.Run($"init --instance {instance}");
            string dbPath = GrimoraCliRunner.InstanceDbPath(instance);
            BrainTestFixtures.InsertNode(dbPath, "tidy:bk", "concept", "backup me", "");

            string backupsDir = Path.Combine(root, "backups");
            Assert.False(Directory.Exists(backupsDir) && Directory.EnumerateFiles(backupsDir).Any());

            using (SqliteConnection connection = StoreConnection.Open(dbPath))
            {
                new BrainTidyTool().Execute(connection, root);
            }

            Assert.True(Directory.Exists(backupsDir));
            string backupPath = Directory.EnumerateFiles(backupsDir).Single();

            using SqliteConnection backup = new($"Data Source={backupPath};Mode=ReadOnly");
            backup.Open();
            using SqliteCommand c = backup.CreateCommand();
            c.CommandText = "SELECT count(*) FROM node_now WHERE k='tidy:bk'";
            Assert.Equal(1L, (long)c.ExecuteScalar()!);
        }
        finally
        {
            GrimoraCliRunner.DeleteInstance(instance);
        }
    }

    private static void SetScheme(string dbPath, string k, string kind, string label, string scheme)
    {
        using SqliteConnection connection = StoreConnection.Open(dbPath);
        using SqliteCommand insert = connection.CreateCommand();
        insert.CommandText = "INSERT INTO node(k,kind,label,scheme) VALUES($k,$kind,$label,$scheme)";
        insert.Parameters.AddWithValue("$k", k);
        insert.Parameters.AddWithValue("$kind", kind);
        insert.Parameters.AddWithValue("$label", label);
        insert.Parameters.AddWithValue("$scheme", scheme);
        insert.ExecuteNonQuery();
    }
}
