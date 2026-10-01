using Grimoira.Brain.Tools;
using Grimoira.Store.Data;
using Grimoira.TestSupport;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Grimoira.Brain.Tests;

public class BrainForgetToolTests
{
    // Oracle: today's grimoira.cs BrainForget (grimoira.cs:1758-1770), reached via CLI `brain forget`.
    // Selftest already covers this behaviour (grimoira.cs:3255-3263: "forget: node + its edges retired
    // (no orphaned reference)"), ported here onto the new tool as 17a did for learn/verify.

    [Fact]
    public void MatchesTodaysCliOutputAndRetiresTheNodeAndItsLiveEdges()
    {
        string oldInstance = GrimoiraCliRunner.NewTestInstance("brain-forget-old");
        string newInstance = GrimoiraCliRunner.NewTestInstance("brain-forget-new");
        try
        {
            GrimoiraCliRunner.Run($"init --instance {oldInstance}");
            string oldDb = GrimoiraCliRunner.InstanceDbPath(oldInstance);
            BrainTestFixtures.InsertNode(oldDb, "forget:target", "concept", "gone", "");
            BrainTestFixtures.InsertNode(oldDb, "forget:other", "concept", "keeper", "");
            BrainTestFixtures.InsertSharingTriple(oldDb, "forget:other", "forget:target");
            (string stdout, int exitCode) = GrimoiraCliRunner.Run($"brain forget --instance {oldInstance} forget:target");
            Assert.Equal(0, exitCode);
            string expected = stdout.Trim();

            GrimoiraCliRunner.Run($"init --instance {newInstance}");
            string newDb = GrimoiraCliRunner.InstanceDbPath(newInstance);
            BrainTestFixtures.InsertNode(newDb, "forget:target", "concept", "gone", "");
            BrainTestFixtures.InsertNode(newDb, "forget:other", "concept", "keeper", "");
            BrainTestFixtures.InsertSharingTriple(newDb, "forget:other", "forget:target");

            string actual;
            using (SqliteConnection connection = StoreConnection.Open(newDb))
            {
                actual = new BrainForgetTool().Execute(connection, GrimoiraCliRunner.InstanceDir(newInstance), "forget:target").Trim();
            }

            Assert.Equal(expected, actual);
            Assert.Equal("forgot forget:target (node + its live edges/slots retired).", actual);

            using SqliteConnection check = StoreConnection.Open(newDb);
            using SqliteCommand nodeCount = check.CreateCommand();
            nodeCount.CommandText = "SELECT count(*) FROM node_now WHERE k='forget:target'";
            Assert.Equal(0L, (long)nodeCount.ExecuteScalar()!);

            using SqliteCommand tripleCount = check.CreateCommand();
            tripleCount.CommandText = "SELECT count(*) FROM triple_now WHERE o='forget:target'";
            Assert.Equal(0L, (long)tripleCount.ExecuteScalar()!);

            using SqliteCommand mutation = check.CreateCommand();
            mutation.CommandText = "SELECT count(*) FROM mutations WHERE kind='node' AND k='forget:target' AND op='forget'";
            Assert.Equal(1L, (long)mutation.ExecuteScalar()!);
        }
        finally
        {
            GrimoiraCliRunner.DeleteInstance(oldInstance);
            GrimoiraCliRunner.DeleteInstance(newInstance);
        }
    }

    [Fact]
    public void ForgettingAMissingNodeReportsNoLiveNodeAndWritesNothing()
    {
        string instance = GrimoiraCliRunner.NewTestInstance("brain-forget-missing");
        try
        {
            GrimoiraCliRunner.Run($"init --instance {instance}");
            string dbPath = GrimoiraCliRunner.InstanceDbPath(instance);

            (string stdout, int exitCode) = GrimoiraCliRunner.Run($"brain forget --instance {instance} ghost:x");
            Assert.Equal(0, exitCode);
            string expected = stdout.Trim();

            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = new BrainForgetTool().Execute(connection, GrimoiraCliRunner.InstanceDir(instance), "ghost:x").Trim();

            Assert.Equal(expected, actual);
            Assert.Equal("no live node 'ghost:x'.", actual);
        }
        finally
        {
            GrimoiraCliRunner.DeleteInstance(instance);
        }
    }

    [Fact]
    public void MakesABackupBeforeForgettingAndTheBackupHoldsTheLiveNode()
    {
        string instance = GrimoiraCliRunner.NewTestInstance("brain-forget-backup");
        string root = GrimoiraCliRunner.InstanceDir(instance);
        try
        {
            GrimoiraCliRunner.Run($"init --instance {instance}");
            string dbPath = GrimoiraCliRunner.InstanceDbPath(instance);
            BrainTestFixtures.InsertNode(dbPath, "forget:bk", "concept", "backup me", "");

            string backupsDir = Path.Combine(root, "backups");
            Assert.False(Directory.Exists(backupsDir) && Directory.EnumerateFiles(backupsDir).Any());

            using (SqliteConnection connection = StoreConnection.Open(dbPath))
            {
                new BrainForgetTool().Execute(connection, root, "forget:bk");
            }

            Assert.True(Directory.Exists(backupsDir));
            string backupPath = Directory.EnumerateFiles(backupsDir).Single();

            using (SqliteConnection backup = new($"Data Source={backupPath};Mode=ReadOnly"))
            {
                backup.Open();
                using SqliteCommand c = backup.CreateCommand();
                c.CommandText = "SELECT count(*) FROM node_now WHERE k='forget:bk'";
                Assert.Equal(1L, (long)c.ExecuteScalar()!);
            }

            using SqliteConnection check = new($"Data Source={dbPath};Mode=ReadOnly");
            check.Open();
            using SqliteCommand live = check.CreateCommand();
            live.CommandText = "SELECT count(*) FROM node_now WHERE k='forget:bk'";
            Assert.Equal(0L, (long)live.ExecuteScalar()!);
        }
        finally
        {
            GrimoiraCliRunner.DeleteInstance(instance);
        }
    }
}
