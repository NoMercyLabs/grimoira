using Grimora.Graph.Tools;
using Grimora.Store.Data;
using Grimora.TestSupport;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Grimora.Graph.Tests;

public class PromoteAllToolTests
{
    [Fact]
    public void MatchesTodaysCliOutputAndReplacesPriorEdgesWithTheCandidateSet()
    {
        string oldInstance = GrimoraCliRunner.NewTestInstance("promote-all-old");
        string newInstance = GrimoraCliRunner.NewTestInstance("promote-all-new");
        try
        {
            // Oracle: today's grimora.cs PromoteAll() (grimora.cs:2999).
            string oldDb = GrimoraCliRunner.InstanceDbPath(oldInstance);
            GrimoraCliRunner.Run($"init --instance {oldInstance}");
            SeedEdge(oldDb, "has_more", "web", "src/old.ts", 1, "old usage");
            SeedCandidate(oldDb, "has_more", "web", "src/new.ts", 2, 1, "page.has_more");
            SeedCandidate(oldDb, "has_more", "api", "src/new2.ts", 3, 0, "");
            (string stdout, int exitCode) = GrimoraCliRunner.Run($"promote-all --instance {oldInstance} --symbol has_more");
            Assert.Equal(0, exitCode);
            string expected = stdout.Trim();

            // New: PromoteAllTool.
            string newDb = GrimoraCliRunner.InstanceDbPath(newInstance);
            string newRoot = GrimoraCliRunner.InstanceDir(newInstance);
            GrimoraCliRunner.Run($"init --instance {newInstance}");
            SeedEdge(newDb, "has_more", "web", "src/old.ts", 1, "old usage");
            SeedCandidate(newDb, "has_more", "web", "src/new.ts", 2, 1, "page.has_more");
            SeedCandidate(newDb, "has_more", "api", "src/new2.ts", 3, 0, "");

            string actual;
            using (SqliteConnection connection = StoreConnection.Open(newDb))
            {
                actual = new PromoteAllTool().Execute(connection, newRoot, "has_more").Trim();
            }

            Assert.Equal(expected, actual);
            Assert.EndsWith("promoted 2 candidate(s) for 'has_more' (replaced 1 prior edge(s)).", actual);

            using SqliteConnection check = new($"Data Source={newDb};Mode=ReadOnly");
            check.Open();
            using SqliteCommand count = check.CreateCommand();
            count.CommandText = "SELECT count(*) FROM edges WHERE symbol='has_more'";
            Assert.Equal(2L, (long)(count.ExecuteScalar() ?? 0L));
            using SqliteCommand oldGone = check.CreateCommand();
            oldGone.CommandText = "SELECT count(*) FROM edges WHERE symbol='has_more' AND file='src/old.ts'";
            Assert.Equal(0L, (long)(oldGone.ExecuteScalar() ?? 0L));
        }
        finally
        {
            GrimoraCliRunner.DeleteInstance(oldInstance);
            GrimoraCliRunner.DeleteInstance(newInstance);
        }
    }

    [Fact]
    public void ReportsNoPendingCandidatesWithoutTouchingTheGraph()
    {
        string instance = GrimoraCliRunner.NewTestInstance("promote-all-none");
        try
        {
            GrimoraCliRunner.Run($"init --instance {instance}");
            string dbPath = GrimoraCliRunner.InstanceDbPath(instance);
            string root = GrimoraCliRunner.InstanceDir(instance);

            (string stdout, int exitCode) = GrimoraCliRunner.Run($"promote-all --instance {instance} --symbol nothing-pending");
            Assert.Equal(0, exitCode);
            string expected = stdout.Trim();

            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = new PromoteAllTool().Execute(connection, root, "nothing-pending").Trim();

            Assert.Equal(expected, actual);
            Assert.Equal("no pending candidates for 'nothing-pending'.", actual);
        }
        finally
        {
            GrimoraCliRunner.DeleteInstance(instance);
        }
    }

    [Fact]
    public void MakesABackupBeforeReplacingAndTheBackupHoldsThePriorEdges()
    {
        // Design checklist (RESTRUCTURE.md section 5): "The CLI admin verbs that delete or bulk-change
        // (forget-project, shed-*, import, spine-import, promote-all, merge) make an automatic backup
        // first." promote-all is named explicitly; slice 11b set the pattern with forget-project.
        string instance = GrimoraCliRunner.NewTestInstance("promote-all-backup");
        string root = GrimoraCliRunner.InstanceDir(instance);
        try
        {
            GrimoraCliRunner.Run($"init --instance {instance}");
            string dbPath = GrimoraCliRunner.InstanceDbPath(instance);
            SeedEdge(dbPath, "has_more", "web", "src/old.ts", 1, "old usage");
            SeedCandidate(dbPath, "has_more", "web", "src/new.ts", 2, 1, "page.has_more");

            string backupsDir = Path.Combine(root, "backups");
            Assert.False(Directory.Exists(backupsDir) && Directory.EnumerateFiles(backupsDir).Any());

            using (SqliteConnection connection = StoreConnection.Open(dbPath))
            {
                new PromoteAllTool().Execute(connection, root, "has_more");
            }

            Assert.True(Directory.Exists(backupsDir));
            string backupPath = Directory.EnumerateFiles(backupsDir).Single();

            using (SqliteConnection backup = new($"Data Source={backupPath};Mode=ReadOnly"))
            {
                backup.Open();
                using SqliteCommand e = backup.CreateCommand();
                e.CommandText = "SELECT count(*) FROM edges WHERE symbol='has_more' AND file='src/old.ts'";
                Assert.Equal(1L, (long)(e.ExecuteScalar() ?? 0L));
            }

            using SqliteConnection check = new($"Data Source={dbPath};Mode=ReadOnly");
            check.Open();
            using SqliteCommand liveOld = check.CreateCommand();
            liveOld.CommandText = "SELECT count(*) FROM edges WHERE symbol='has_more' AND file='src/old.ts'";
            Assert.Equal(0L, (long)(liveOld.ExecuteScalar() ?? 0L));
        }
        finally
        {
            GrimoraCliRunner.DeleteInstance(instance);
        }
    }

    private static void SeedEdge(string dbPath, string symbol, string project, string file, int line, string usage)
    {
        using SqliteConnection connection = StoreConnection.Open(dbPath);
        using SqliteCommand insert = connection.CreateCommand();
        insert.CommandText = "INSERT INTO edges(symbol,contract,project,file,line,usage,hardcoded) VALUES($s,'C',$p,$f,$l,$u,0)";
        insert.Parameters.AddWithValue("$s", symbol);
        insert.Parameters.AddWithValue("$p", project);
        insert.Parameters.AddWithValue("$f", file);
        insert.Parameters.AddWithValue("$l", line);
        insert.Parameters.AddWithValue("$u", usage);
        insert.ExecuteNonQuery();
    }

    private static void SeedCandidate(string dbPath, string symbol, string project, string file, int line, int hardcoded, string usage)
    {
        using SqliteConnection connection = StoreConnection.Open(dbPath);
        using SqliteCommand insert = connection.CreateCommand();
        insert.CommandText = "INSERT INTO edge_candidates(symbol,contract,project,file,line,usage,hardcoded,status) VALUES($s,'C',$p,$f,$l,$u,$h,'pending')";
        insert.Parameters.AddWithValue("$s", symbol);
        insert.Parameters.AddWithValue("$p", project);
        insert.Parameters.AddWithValue("$f", file);
        insert.Parameters.AddWithValue("$l", line);
        insert.Parameters.AddWithValue("$u", usage);
        insert.Parameters.AddWithValue("$h", hardcoded);
        insert.ExecuteNonQuery();
    }
}
