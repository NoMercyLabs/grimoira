using Grimoira.Brain.Tools;
using Grimoira.Store.Data;
using Grimoira.TestSupport;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Grimoira.Brain.Tests;

public class BrainUnlinkToolTests
{
    // Oracle: today's grimoira.cs BrainUnlink (grimoira.cs:1742-1754), reached via CLI `brain unlink`.

    [Fact]
    public void MatchesTodaysCliOutputAndRetiresOnlyTheNamedTriple()
    {
        string oldInstance = GrimoiraCliRunner.NewTestInstance("brain-unlink-old");
        string newInstance = GrimoiraCliRunner.NewTestInstance("brain-unlink-new");
        try
        {
            GrimoiraCliRunner.Run($"init --instance {oldInstance}");
            string oldDb = GrimoiraCliRunner.InstanceDbPath(oldInstance);
            BrainTestFixtures.InsertNode(oldDb, "unlink:s", "concept", "subject", "");
            BrainTestFixtures.InsertNode(oldDb, "unlink:o", "concept", "object", "");
            BrainTestFixtures.InsertSharingTriple(oldDb, "unlink:s", "unlink:o");
            (string stdout, int exitCode) = GrimoiraCliRunner.Run($"brain unlink --instance {oldInstance} unlink:s consumes unlink:o");
            Assert.Equal(0, exitCode);
            string expected = stdout.Trim();

            GrimoiraCliRunner.Run($"init --instance {newInstance}");
            string newDb = GrimoiraCliRunner.InstanceDbPath(newInstance);
            BrainTestFixtures.InsertNode(newDb, "unlink:s", "concept", "subject", "");
            BrainTestFixtures.InsertNode(newDb, "unlink:o", "concept", "object", "");
            BrainTestFixtures.InsertSharingTriple(newDb, "unlink:s", "unlink:o");

            string actual;
            using (SqliteConnection connection = StoreConnection.Open(newDb))
            {
                actual = new BrainUnlinkTool().Execute(connection, GrimoiraCliRunner.InstanceDir(newInstance), "unlink:s", "consumes", "unlink:o").Trim();
            }

            Assert.Equal(expected, actual);
            Assert.Equal("unlinked unlink:s consumes unlink:o.", actual);

            using SqliteConnection check = StoreConnection.Open(newDb);
            using SqliteCommand count = check.CreateCommand();
            count.CommandText = "SELECT count(*) FROM triple_now WHERE s='unlink:s' AND p='consumes' AND o='unlink:o'";
            Assert.Equal(0L, (long)count.ExecuteScalar()!);

            using SqliteCommand mutation = check.CreateCommand();
            mutation.CommandText = "SELECT count(*) FROM mutations WHERE kind='triple' AND k='unlink:s consumes unlink:o' AND op='unlink'";
            Assert.Equal(1L, (long)mutation.ExecuteScalar()!);
        }
        finally
        {
            GrimoiraCliRunner.DeleteInstance(oldInstance);
            GrimoiraCliRunner.DeleteInstance(newInstance);
        }
    }

    [Fact]
    public void UnlinkingAMissingTripleReportsNoLiveTripleAndWritesNothing()
    {
        string instance = GrimoiraCliRunner.NewTestInstance("brain-unlink-missing");
        try
        {
            GrimoiraCliRunner.Run($"init --instance {instance}");
            string dbPath = GrimoiraCliRunner.InstanceDbPath(instance);

            (string stdout, int exitCode) = GrimoiraCliRunner.Run($"brain unlink --instance {instance} ghost:s ghost ghost:o");
            Assert.Equal(0, exitCode);
            string expected = stdout.Trim();

            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = new BrainUnlinkTool().Execute(connection, GrimoiraCliRunner.InstanceDir(instance), "ghost:s", "ghost", "ghost:o").Trim();

            Assert.Equal(expected, actual);
            Assert.Equal("no live triple 'ghost:s ghost ghost:o'.", actual);
        }
        finally
        {
            GrimoiraCliRunner.DeleteInstance(instance);
        }
    }

    [Fact]
    public void MakesABackupBeforeUnlinkingAndTheBackupHoldsTheLiveTriple()
    {
        // Design checklist (RESTRUCTURE.md section 5): a CLI admin verb that deletes/bulk-changes backs
        // up first (as forget-project).
        string instance = GrimoiraCliRunner.NewTestInstance("brain-unlink-backup");
        string root = GrimoiraCliRunner.InstanceDir(instance);
        try
        {
            GrimoiraCliRunner.Run($"init --instance {instance}");
            string dbPath = GrimoiraCliRunner.InstanceDbPath(instance);
            BrainTestFixtures.InsertNode(dbPath, "unlink:bs", "concept", "subject", "");
            BrainTestFixtures.InsertNode(dbPath, "unlink:bo", "concept", "object", "");
            BrainTestFixtures.InsertSharingTriple(dbPath, "unlink:bs", "unlink:bo");

            string backupsDir = Path.Combine(root, "backups");
            Assert.False(Directory.Exists(backupsDir) && Directory.EnumerateFiles(backupsDir).Any());

            using (SqliteConnection connection = StoreConnection.Open(dbPath))
            {
                new BrainUnlinkTool().Execute(connection, root, "unlink:bs", "consumes", "unlink:bo");
            }

            Assert.True(Directory.Exists(backupsDir));
            string backupPath = Directory.EnumerateFiles(backupsDir).Single();

            using (SqliteConnection backup = new($"Data Source={backupPath};Mode=ReadOnly"))
            {
                backup.Open();
                using SqliteCommand c = backup.CreateCommand();
                c.CommandText = "SELECT count(*) FROM triple_now WHERE s='unlink:bs' AND p='consumes' AND o='unlink:bo'";
                Assert.Equal(1L, (long)c.ExecuteScalar()!);
            }

            using SqliteConnection check = new($"Data Source={dbPath};Mode=ReadOnly");
            check.Open();
            using SqliteCommand live = check.CreateCommand();
            live.CommandText = "SELECT count(*) FROM triple_now WHERE s='unlink:bs' AND p='consumes' AND o='unlink:bo'";
            Assert.Equal(0L, (long)live.ExecuteScalar()!);
        }
        finally
        {
            GrimoiraCliRunner.DeleteInstance(instance);
        }
    }
}
