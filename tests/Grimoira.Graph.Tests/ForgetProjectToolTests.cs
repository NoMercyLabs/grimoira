using Grimoira.Graph.Tools;
using Grimoira.Store.Data;
using Grimoira.TestSupport;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Grimoira.Graph.Tests;

public class ForgetProjectToolTests
{
    [Fact]
    public void MatchesTodaysCliOutputAndDropsOnlyTheNamedProjectsRows()
    {
        string oldInstance = GrimoiraCliRunner.NewTestInstance("forget-project-old");
        string newInstance = GrimoiraCliRunner.NewTestInstance("forget-project-new");
        try
        {
            // Oracle: today's grimoira.cs case "forget-project" (grimoira.cs:172-180).
            GrimoiraCliRunner.Run($"init --instance {oldInstance}");
            GrimoiraCliRunner.Run($"project --instance {oldInstance} --name web --root /repo/web");
            GrimoiraCliRunner.Run($"project --instance {oldInstance} --name api --root /repo/api");
            SeedEdges(GrimoiraCliRunner.InstanceDbPath(oldInstance), "web", 2);
            SeedEdges(GrimoiraCliRunner.InstanceDbPath(oldInstance), "api", 3);
            (string stdout, int exitCode) = GrimoiraCliRunner.Run($"forget-project --instance {oldInstance} --name web");
            Assert.Equal(0, exitCode);
            string expected = stdout.Trim();

            // New: ForgetProjectTool.
            GrimoiraCliRunner.Run($"init --instance {newInstance}");
            string dbPath = GrimoiraCliRunner.InstanceDbPath(newInstance);
            using (SqliteConnection setup = StoreConnection.Open(dbPath))
            {
                new ProjectTool().Execute(setup, "web", "/repo/web", "", "*.ts,*.tsx,*.vue,*.kt,*.cs");
                new ProjectTool().Execute(setup, "api", "/repo/api", "", "*.ts,*.tsx,*.vue,*.kt,*.cs");
            }
            SeedEdges(dbPath, "web", 2);
            SeedEdges(dbPath, "api", 3);

            string actual;
            using (SqliteConnection connection = StoreConnection.Open(dbPath))
            {
                actual = new ForgetProjectTool().Execute(connection, GrimoiraCliRunner.InstanceDir(newInstance), "web").Trim();
            }

            Assert.Equal(expected, actual);
            Assert.Equal("project 'web' forgotten (2 edge(s) dropped).", actual);

            using SqliteConnection check = new($"Data Source={dbPath};Mode=ReadOnly");
            check.Open();

            // Only "web" is gone: its project row and its edges.
            using (SqliteCommand p = check.CreateCommand())
            {
                p.CommandText = "SELECT count(*) FROM projects WHERE name='web'";
                Assert.Equal(0L, (long)(p.ExecuteScalar() ?? 0L));
            }
            using (SqliteCommand e = check.CreateCommand())
            {
                e.CommandText = "SELECT count(*) FROM edges WHERE project='web'";
                Assert.Equal(0L, (long)(e.ExecuteScalar() ?? 0L));
            }

            // "api" is untouched: its project row and all 3 of its edges survive.
            using (SqliteCommand p = check.CreateCommand())
            {
                p.CommandText = "SELECT count(*) FROM projects WHERE name='api'";
                Assert.Equal(1L, (long)(p.ExecuteScalar() ?? 0L));
            }
            using (SqliteCommand e = check.CreateCommand())
            {
                e.CommandText = "SELECT count(*) FROM edges WHERE project='api'";
                Assert.Equal(3L, (long)(e.ExecuteScalar() ?? 0L));
            }
        }
        finally
        {
            GrimoiraCliRunner.DeleteInstance(oldInstance);
            GrimoiraCliRunner.DeleteInstance(newInstance);
        }
    }

    [Fact]
    public void ForgettingAnUnknownProjectReportsZeroEdgesDropped()
    {
        // Two instances, not one: grimoira.cs's own "forget-project" case calls this same ForgetProjectTool
        // (RESTRUCTURE.md slice 24, CLI lane part 3), so the CLI run below and the direct call further
        // down each take their own BackupTool snapshot. BackupTool's default filename only has
        // second-resolution, so one instance running both within the same second collided with itself
        // ("output file already exists") — the same isolation test #1 above already uses, for the same
        // reason.
        string oldInstance = GrimoiraCliRunner.NewTestInstance("forget-project-missing-old");
        string newInstance = GrimoiraCliRunner.NewTestInstance("forget-project-missing-new");
        try
        {
            GrimoiraCliRunner.Run($"init --instance {oldInstance}");
            (string stdout, int exitCode) = GrimoiraCliRunner.Run($"forget-project --instance {oldInstance} --name ghost");
            Assert.Equal(0, exitCode);
            string expected = stdout.Trim();

            GrimoiraCliRunner.Run($"init --instance {newInstance}");
            string dbPath = GrimoiraCliRunner.InstanceDbPath(newInstance);
            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = new ForgetProjectTool().Execute(connection, GrimoiraCliRunner.InstanceDir(newInstance), "ghost").Trim();

            Assert.Equal(expected, actual);
            Assert.Equal("project 'ghost' forgotten (0 edge(s) dropped).", actual);
        }
        finally
        {
            GrimoiraCliRunner.DeleteInstance(oldInstance);
            GrimoiraCliRunner.DeleteInstance(newInstance);
        }
    }

    [Fact]
    public void MakesABackupBeforeDeletingAndTheBackupHoldsTheProjectsRows()
    {
        // Design checklist (RESTRUCTURE.md section 5): "The CLI admin verbs that delete or bulk-change
        // ... make an automatic backup first." forget-project is the first delete verb that moved, so
        // slice 11b gives it the rule.
        string instance = GrimoiraCliRunner.NewTestInstance("forget-project-backup");
        string root = GrimoiraCliRunner.InstanceDir(instance);
        try
        {
            GrimoiraCliRunner.Run($"init --instance {instance}");
            string dbPath = GrimoiraCliRunner.InstanceDbPath(instance);
            using (SqliteConnection setup = StoreConnection.Open(dbPath))
            {
                new ProjectTool().Execute(setup, "web", "/repo/web", "", "*.ts");
            }
            SeedEdges(dbPath, "web", 2);

            string backupsDir = Path.Combine(root, "backups");
            Assert.False(Directory.Exists(backupsDir) && Directory.EnumerateFiles(backupsDir).Any());

            using (SqliteConnection connection = StoreConnection.Open(dbPath))
            {
                new ForgetProjectTool().Execute(connection, root, "web");
            }

            Assert.True(Directory.Exists(backupsDir));
            string backupPath = Directory.EnumerateFiles(backupsDir).Single();

            // The backup holds "web"'s rows ...
            using (SqliteConnection backup = new($"Data Source={backupPath};Mode=ReadOnly"))
            {
                backup.Open();
                using SqliteCommand p = backup.CreateCommand();
                p.CommandText = "SELECT count(*) FROM projects WHERE name='web'";
                Assert.Equal(1L, (long)(p.ExecuteScalar() ?? 0L));
                using SqliteCommand e = backup.CreateCommand();
                e.CommandText = "SELECT count(*) FROM edges WHERE project='web'";
                Assert.Equal(2L, (long)(e.ExecuteScalar() ?? 0L));
            }

            // ... while the live store no longer does.
            using SqliteConnection check = new($"Data Source={dbPath};Mode=ReadOnly");
            check.Open();
            using SqliteCommand liveP = check.CreateCommand();
            liveP.CommandText = "SELECT count(*) FROM projects WHERE name='web'";
            Assert.Equal(0L, (long)(liveP.ExecuteScalar() ?? 0L));
        }
        finally
        {
            GrimoiraCliRunner.DeleteInstance(instance);
        }
    }

    [Fact]
    public void TwoDeletesInARowThenRestoringFromTheFirstBackupBringsBackTheFirstDeletesRows()
    {
        // Bug: BackupTool's default (automatic) name only had second-resolution and always
        // File.Move(overwrite: true) into place. Two forget-project calls back-to-back landed their
        // automatic backups in the same second, so the second overwrote the first: the rows the first
        // delete removed ("web"'s project row and edges) were unrecoverable. The fix must make each
        // automatic backup a distinct file, so the pre-first-delete snapshot survives the second delete.
        string instance = GrimoiraCliRunner.NewTestInstance("forget-project-restore");
        string root = GrimoiraCliRunner.InstanceDir(instance);
        try
        {
            GrimoiraCliRunner.Run($"init --instance {instance}");
            string dbPath = GrimoiraCliRunner.InstanceDbPath(instance);
            using (SqliteConnection setup = StoreConnection.Open(dbPath))
            {
                new ProjectTool().Execute(setup, "web", "/repo/web", "", "*.ts");
                new ProjectTool().Execute(setup, "api", "/repo/api", "", "*.ts");
            }
            SeedEdges(dbPath, "web", 2);
            SeedEdges(dbPath, "api", 3);

            string backupsDir = Path.Combine(root, "backups");

            // First delete: its automatic backup, taken before the delete, still holds "web".
            using (SqliteConnection connection = StoreConnection.Open(dbPath))
            {
                new ForgetProjectTool().Execute(connection, root, "web");
            }
            string[] afterFirstDelete = [.. Directory.EnumerateFiles(backupsDir).OrderBy(p => p, StringComparer.Ordinal)];
            Assert.Single(afterFirstDelete);
            string firstBackupPath = afterFirstDelete[0];

            // Second delete, right after the first: its own automatic backup must not collide with (and
            // so must not overwrite) the first one.
            using (SqliteConnection connection = StoreConnection.Open(dbPath))
            {
                new ForgetProjectTool().Execute(connection, root, "api");
            }
            string[] afterSecondDelete = [.. Directory.EnumerateFiles(backupsDir).OrderBy(p => p, StringComparer.Ordinal)];
            Assert.Equal(2, afterSecondDelete.Length);
            Assert.Contains(firstBackupPath, afterSecondDelete);

            // Restoring from the first backup brings back "web"'s project row and both its edges.
            using SqliteConnection restored = new($"Data Source={firstBackupPath};Mode=ReadOnly");
            restored.Open();
            using (SqliteCommand p = restored.CreateCommand())
            {
                p.CommandText = "SELECT count(*) FROM projects WHERE name='web'";
                Assert.Equal(1L, (long)(p.ExecuteScalar() ?? 0L));
            }
            using (SqliteCommand e = restored.CreateCommand())
            {
                e.CommandText = "SELECT count(*) FROM edges WHERE project='web'";
                Assert.Equal(2L, (long)(e.ExecuteScalar() ?? 0L));
            }
        }
        finally
        {
            GrimoiraCliRunner.DeleteInstance(instance);
        }
    }

    /// <summary>Inserts <paramref name="count"/> raw edge rows for a project. There is no standalone
    /// "add one edge" CLI verb today — edges only arrive through extract-edges/promote (candidate review)
    /// or seed-edges (the curated seed) — so the fixture writes the row shape directly, matching
    /// GraphSchema's <c>edges</c> table (src/Grimoira.Graph/Schema/GraphSchema.cs).</summary>
    private static void SeedEdges(string dbPath, string project, int count)
    {
        using SqliteConnection connection = StoreConnection.Open(dbPath);
        for (int i = 0; i < count; i++)
        {
            using SqliteCommand insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO edges(symbol,contract,project,file,line,usage,hardcoded) " +
                "VALUES($s,$c,$p,$f,$l,$u,0)";
            insert.Parameters.AddWithValue("$s", $"symbol{i}");
            insert.Parameters.AddWithValue("$c", "SomeContract");
            insert.Parameters.AddWithValue("$p", project);
            insert.Parameters.AddWithValue("$f", $"src/file{i}.ts");
            insert.Parameters.AddWithValue("$l", i + 1);
            insert.Parameters.AddWithValue("$u", $"usage{i}");
            insert.ExecuteNonQuery();
        }
    }
}
