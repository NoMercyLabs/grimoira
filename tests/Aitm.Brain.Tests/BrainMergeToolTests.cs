using Aitm.Brain.Tools;
using Aitm.Store.Data;
using Aitm.TestSupport;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Aitm.Brain.Tests;

public class BrainMergeToolTests
{
    // Oracle: today's aitm.cs BrainMerge (aitm.cs:1806-1834), reached via CLI `brain merge`.
    // Selftest already covers this behaviour (aitm.cs:3244-3252: "merge: source retired, edge
    // re-pointed, no dead reference"), ported here onto the new tool as 17a did for learn/verify.

    [Fact]
    public void MatchesTodaysCliOutputAndRepointsTheEdgeWithNoDeadReference()
    {
        string oldInstance = AitmCliRunner.NewTestInstance("brain-merge-old");
        string newInstance = AitmCliRunner.NewTestInstance("brain-merge-new");
        try
        {
            AitmCliRunner.Run($"init --instance {oldInstance}");
            string oldDb = AitmCliRunner.InstanceDbPath(oldInstance);
            BrainTestFixtures.InsertNode(oldDb, "merge:src", "concept", "Dup Source", "");
            BrainTestFixtures.InsertNode(oldDb, "merge:dst", "concept", "Dup Target", "");
            BrainTestFixtures.InsertNode(oldDb, "merge:other", "concept", "Referrer", "");
            BrainTestFixtures.InsertSharingTriple(oldDb, "merge:other", "merge:src");
            (string stdout, int exitCode) = AitmCliRunner.Run($"brain merge --instance {oldInstance} merge:src merge:dst");
            Assert.Equal(0, exitCode);
            string expected = stdout.Trim();

            AitmCliRunner.Run($"init --instance {newInstance}");
            string newDb = AitmCliRunner.InstanceDbPath(newInstance);
            BrainTestFixtures.InsertNode(newDb, "merge:src", "concept", "Dup Source", "");
            BrainTestFixtures.InsertNode(newDb, "merge:dst", "concept", "Dup Target", "");
            BrainTestFixtures.InsertNode(newDb, "merge:other", "concept", "Referrer", "");
            BrainTestFixtures.InsertSharingTriple(newDb, "merge:other", "merge:src");

            string actual;
            using (SqliteConnection connection = StoreConnection.Open(newDb))
            {
                actual = new BrainMergeTool().Execute(connection, AitmCliRunner.InstanceDir(newInstance), "merge:src", "merge:dst").Trim();
            }

            Assert.Equal(expected, actual);
            Assert.Equal("merged merge:src -> merge:dst.", actual);

            using SqliteConnection check = StoreConnection.Open(newDb);
            using SqliteCommand srcCount = check.CreateCommand();
            srcCount.CommandText = "SELECT count(*) FROM node_now WHERE k='merge:src'";
            Assert.Equal(0L, (long)srcCount.ExecuteScalar()!);

            using SqliteCommand repointed = check.CreateCommand();
            repointed.CommandText = "SELECT count(*) FROM triple_now WHERE s='merge:other' AND p='consumes' AND o='merge:dst'";
            Assert.Equal(1L, (long)repointed.ExecuteScalar()!);

            using SqliteCommand dead = check.CreateCommand();
            dead.CommandText = "SELECT count(*) FROM triple_now WHERE o='merge:src'";
            Assert.Equal(0L, (long)dead.ExecuteScalar()!);

            using SqliteCommand mutation = check.CreateCommand();
            mutation.CommandText = "SELECT count(*) FROM mutations WHERE kind='node' AND k='merge:src' AND op='merge'";
            Assert.Equal(1L, (long)mutation.ExecuteScalar()!);
        }
        finally
        {
            AitmCliRunner.DeleteInstance(oldInstance);
            AitmCliRunner.DeleteInstance(newInstance);
        }
    }

    [Fact]
    public void MergingANonLiveTargetReportsItIsNotALiveNodeAndWritesNothing()
    {
        string instance = AitmCliRunner.NewTestInstance("brain-merge-missing-target");
        try
        {
            AitmCliRunner.Run($"init --instance {instance}");
            string dbPath = AitmCliRunner.InstanceDbPath(instance);
            BrainTestFixtures.InsertNode(dbPath, "merge:realsrc", "concept", "real", "");

            (string stdout, int exitCode) = AitmCliRunner.Run($"brain merge --instance {instance} merge:realsrc ghost:dst");
            Assert.Equal(0, exitCode);
            string expected = stdout.Trim();

            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = new BrainMergeTool().Execute(connection, AitmCliRunner.InstanceDir(instance), "merge:realsrc", "ghost:dst").Trim();

            Assert.Equal(expected, actual);
            Assert.Equal("target 'ghost:dst' is not a live node.", actual);
        }
        finally
        {
            AitmCliRunner.DeleteInstance(instance);
        }
    }

    [Fact]
    public void MakesABackupBeforeMergingAndTheBackupHoldsTheSourceNode()
    {
        string instance = AitmCliRunner.NewTestInstance("brain-merge-backup");
        string root = AitmCliRunner.InstanceDir(instance);
        try
        {
            AitmCliRunner.Run($"init --instance {instance}");
            string dbPath = AitmCliRunner.InstanceDbPath(instance);
            BrainTestFixtures.InsertNode(dbPath, "merge:bsrc", "concept", "backup src", "");
            BrainTestFixtures.InsertNode(dbPath, "merge:bdst", "concept", "backup dst", "");

            string backupsDir = Path.Combine(root, "backups");
            Assert.False(Directory.Exists(backupsDir) && Directory.EnumerateFiles(backupsDir).Any());

            using (SqliteConnection connection = StoreConnection.Open(dbPath))
            {
                new BrainMergeTool().Execute(connection, root, "merge:bsrc", "merge:bdst");
            }

            Assert.True(Directory.Exists(backupsDir));
            string backupPath = Directory.EnumerateFiles(backupsDir).Single();

            using (SqliteConnection backup = new($"Data Source={backupPath};Mode=ReadOnly"))
            {
                backup.Open();
                using SqliteCommand c = backup.CreateCommand();
                c.CommandText = "SELECT count(*) FROM node_now WHERE k='merge:bsrc'";
                Assert.Equal(1L, (long)c.ExecuteScalar()!);
            }

            using SqliteConnection check = new($"Data Source={dbPath};Mode=ReadOnly");
            check.Open();
            using SqliteCommand live = check.CreateCommand();
            live.CommandText = "SELECT count(*) FROM node_now WHERE k='merge:bsrc'";
            Assert.Equal(0L, (long)live.ExecuteScalar()!);
        }
        finally
        {
            AitmCliRunner.DeleteInstance(instance);
        }
    }
}
