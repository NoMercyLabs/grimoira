using Grimora.Brain.Tools;
using Grimora.Store.Data;
using Grimora.TestSupport;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Grimora.Brain.Tests;

public class BrainUnlinkToolTests
{
    // Oracle: today's grimora.cs BrainUnlink (grimora.cs:1742-1754), reached via CLI `brain unlink`.

    [Fact]
    public void MatchesTodaysCliOutputAndRetiresOnlyTheNamedTriple()
    {
        string oldInstance = GrimoraCliRunner.NewTestInstance("brain-unlink-old");
        string newInstance = GrimoraCliRunner.NewTestInstance("brain-unlink-new");
        try
        {
            GrimoraCliRunner.Run($"init --instance {oldInstance}");
            string oldDb = GrimoraCliRunner.InstanceDbPath(oldInstance);
            BrainTestFixtures.InsertNode(oldDb, "unlink:s", "concept", "subject", "");
            BrainTestFixtures.InsertNode(oldDb, "unlink:o", "concept", "object", "");
            BrainTestFixtures.InsertSharingTriple(oldDb, "unlink:s", "unlink:o");
            (string stdout, int exitCode) = GrimoraCliRunner.Run($"brain unlink --instance {oldInstance} unlink:s consumes unlink:o");
            Assert.Equal(0, exitCode);
            string expected = stdout.Trim();

            GrimoraCliRunner.Run($"init --instance {newInstance}");
            string newDb = GrimoraCliRunner.InstanceDbPath(newInstance);
            BrainTestFixtures.InsertNode(newDb, "unlink:s", "concept", "subject", "");
            BrainTestFixtures.InsertNode(newDb, "unlink:o", "concept", "object", "");
            BrainTestFixtures.InsertSharingTriple(newDb, "unlink:s", "unlink:o");

            string actual;
            using (SqliteConnection connection = StoreConnection.Open(newDb))
            {
                actual = new BrainUnlinkTool().Execute(connection, GrimoraCliRunner.InstanceDir(newInstance), "unlink:s", "consumes", "unlink:o").Trim();
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
            GrimoraCliRunner.DeleteInstance(oldInstance);
            GrimoraCliRunner.DeleteInstance(newInstance);
        }
    }

    [Fact]
    public void UnlinkingAMissingTripleReportsNoLiveTripleAndWritesNothing()
    {
        string instance = GrimoraCliRunner.NewTestInstance("brain-unlink-missing");
        try
        {
            GrimoraCliRunner.Run($"init --instance {instance}");
            string dbPath = GrimoraCliRunner.InstanceDbPath(instance);

            (string stdout, int exitCode) = GrimoraCliRunner.Run($"brain unlink --instance {instance} ghost:s ghost ghost:o");
            Assert.Equal(0, exitCode);
            string expected = stdout.Trim();

            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = new BrainUnlinkTool().Execute(connection, GrimoraCliRunner.InstanceDir(instance), "ghost:s", "ghost", "ghost:o").Trim();

            Assert.Equal(expected, actual);
            Assert.Equal("no live triple 'ghost:s ghost ghost:o'.", actual);
        }
        finally
        {
            GrimoraCliRunner.DeleteInstance(instance);
        }
    }

    [Fact]
    public void MakesABackupBeforeUnlinkingAndTheBackupHoldsTheLiveTriple()
    {
        // Design checklist (RESTRUCTURE.md section 5): a CLI admin verb that deletes/bulk-changes backs
        // up first (as forget-project).
        string instance = GrimoraCliRunner.NewTestInstance("brain-unlink-backup");
        string root = GrimoraCliRunner.InstanceDir(instance);
        try
        {
            GrimoraCliRunner.Run($"init --instance {instance}");
            string dbPath = GrimoraCliRunner.InstanceDbPath(instance);
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
            GrimoraCliRunner.DeleteInstance(instance);
        }
    }
}
