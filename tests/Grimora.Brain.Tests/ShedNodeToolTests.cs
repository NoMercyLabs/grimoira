using Grimora.Brain.Tools;
using Grimora.Store.Data;
using Grimora.TestSupport;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Grimora.Brain.Tests;

public class ShedNodeToolTests
{
    // Oracle: today's grimora.cs case "shed-node" (grimora.cs:145-156).

    [Fact]
    public void MatchesTodaysCliOutputAndRetiresTheNodeAndItsLinks()
    {
        string oldInstance = GrimoraCliRunner.NewTestInstance("shed-node-old");
        string newInstance = GrimoraCliRunner.NewTestInstance("shed-node-new");
        try
        {
            GrimoraCliRunner.Run($"init --instance {oldInstance}");
            string oldDb = GrimoraCliRunner.InstanceDbPath(oldInstance);
            BrainTestFixtures.InsertNode(oldDb, "shed:target", "concept", "gone", "");
            BrainTestFixtures.InsertNode(oldDb, "shed:other", "concept", "keeper", "");
            BrainTestFixtures.InsertSharingTriple(oldDb, "shed:other", "shed:target");
            (string stdout, int exitCode) = GrimoraCliRunner.Run($"shed-node --instance {oldInstance} --key shed:target");
            Assert.Equal(0, exitCode);
            string expected = stdout.Trim();

            GrimoraCliRunner.Run($"init --instance {newInstance}");
            string newDb = GrimoraCliRunner.InstanceDbPath(newInstance);
            BrainTestFixtures.InsertNode(newDb, "shed:target", "concept", "gone", "");
            BrainTestFixtures.InsertNode(newDb, "shed:other", "concept", "keeper", "");
            BrainTestFixtures.InsertSharingTriple(newDb, "shed:other", "shed:target");

            string actual;
            using (SqliteConnection connection = StoreConnection.Open(newDb))
            {
                actual = new ShedNodeTool().Execute(connection, GrimoraCliRunner.InstanceDir(newInstance), "shed:target").Trim();
            }

            Assert.Equal(expected, actual);
            Assert.Equal("retired node 'shed:target' and its links.", actual);

            using SqliteConnection check = StoreConnection.Open(newDb);
            using SqliteCommand nodeCount = check.CreateCommand();
            nodeCount.CommandText = "SELECT count(*) FROM node_now WHERE k='shed:target'";
            Assert.Equal(0L, (long)nodeCount.ExecuteScalar()!);

            using SqliteCommand tripleCount = check.CreateCommand();
            tripleCount.CommandText = "SELECT count(*) FROM triple_now WHERE o='shed:target'";
            Assert.Equal(0L, (long)tripleCount.ExecuteScalar()!);
        }
        finally
        {
            GrimoraCliRunner.DeleteInstance(oldInstance);
            GrimoraCliRunner.DeleteInstance(newInstance);
        }
    }

    [Fact]
    public void MakesABackupBeforeSheddingAndTheBackupHoldsTheLiveNode()
    {
        string instance = GrimoraCliRunner.NewTestInstance("shed-node-backup");
        string root = GrimoraCliRunner.InstanceDir(instance);
        try
        {
            GrimoraCliRunner.Run($"init --instance {instance}");
            string dbPath = GrimoraCliRunner.InstanceDbPath(instance);
            BrainTestFixtures.InsertNode(dbPath, "shed:bk", "concept", "backup me", "");

            string backupsDir = Path.Combine(root, "backups");
            Assert.False(Directory.Exists(backupsDir) && Directory.EnumerateFiles(backupsDir).Any());

            using (SqliteConnection connection = StoreConnection.Open(dbPath))
            {
                new ShedNodeTool().Execute(connection, root, "shed:bk");
            }

            Assert.True(Directory.Exists(backupsDir));
            string backupPath = Directory.EnumerateFiles(backupsDir).Single();

            using (SqliteConnection backup = new($"Data Source={backupPath};Mode=ReadOnly"))
            {
                backup.Open();
                using SqliteCommand c = backup.CreateCommand();
                c.CommandText = "SELECT count(*) FROM node_now WHERE k='shed:bk'";
                Assert.Equal(1L, (long)c.ExecuteScalar()!);
            }

            using SqliteConnection check = new($"Data Source={dbPath};Mode=ReadOnly");
            check.Open();
            using SqliteCommand live = check.CreateCommand();
            live.CommandText = "SELECT count(*) FROM node_now WHERE k='shed:bk'";
            Assert.Equal(0L, (long)live.ExecuteScalar()!);
        }
        finally
        {
            GrimoraCliRunner.DeleteInstance(instance);
        }
    }
}
