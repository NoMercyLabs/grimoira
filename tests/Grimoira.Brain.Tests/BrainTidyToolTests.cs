using Grimoira.Brain.Tools;
using Grimoira.Store.Data;
using Grimoira.TestSupport;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Grimoira.Brain.Tests;

public class BrainTidyToolTests
{
    // Oracle: today's grimoira.cs BrainTidy (grimoira.cs:1899-1906), reached via CLI `brain tidy`.

    [Fact]
    public void MatchesTodaysCliOutputAndBackfillsSchemeFromKindOnSchemelessNodesOnly()
    {
        string oldInstance = GrimoiraCliRunner.NewTestInstance("brain-tidy-old");
        string newInstance = GrimoiraCliRunner.NewTestInstance("brain-tidy-new");
        try
        {
            GrimoiraCliRunner.Run($"init --instance {oldInstance}");
            string oldDb = GrimoiraCliRunner.InstanceDbPath(oldInstance);
            BrainTestFixtures.InsertNode(oldDb, "tidy:bare", "concept", "no scheme", "");
            SetScheme(oldDb, "tidy:has", "concept", "has scheme", "custom");
            (string stdout, int exitCode) = GrimoiraCliRunner.Run($"brain tidy --instance {oldInstance}");
            Assert.Equal(0, exitCode);
            string expected = stdout.Trim();

            GrimoiraCliRunner.Run($"init --instance {newInstance}");
            string newDb = GrimoiraCliRunner.InstanceDbPath(newInstance);
            BrainTestFixtures.InsertNode(newDb, "tidy:bare", "concept", "no scheme", "");
            SetScheme(newDb, "tidy:has", "concept", "has scheme", "custom");

            string actual;
            using (SqliteConnection connection = StoreConnection.Open(newDb))
            {
                actual = new BrainTidyTool().Execute(connection, GrimoiraCliRunner.InstanceDir(newInstance)).Trim();
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
            GrimoiraCliRunner.DeleteInstance(oldInstance);
            GrimoiraCliRunner.DeleteInstance(newInstance);
        }
    }

    [Fact]
    public void MakesABackupBeforeTidying()
    {
        string instance = GrimoiraCliRunner.NewTestInstance("brain-tidy-backup");
        string root = GrimoiraCliRunner.InstanceDir(instance);
        try
        {
            GrimoiraCliRunner.Run($"init --instance {instance}");
            string dbPath = GrimoiraCliRunner.InstanceDbPath(instance);
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
            GrimoiraCliRunner.DeleteInstance(instance);
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
