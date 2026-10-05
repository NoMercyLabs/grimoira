using Grimoira.Graph.Tools;
using Grimoira.Store.Data;
using Grimoira.TestSupport;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Grimoira.Graph.Tests;

public class ProjectToolTests
{
    [Fact]
    public void MatchesTodaysCliOutputAndRegistersTheProject()
    {
        string oldInstance = GrimoiraCliRunner.NewTestInstance("project-old");
        string newInstance = GrimoiraCliRunner.NewTestInstance("project-new");
        try
        {
            // Oracle: today's grimoira.cs case "project" (grimoira.cs:160-166).
            GrimoiraCliRunner.Run($"init --instance {oldInstance}");
            (string stdout, int exitCode) = GrimoiraCliRunner.Run(
                $"project --instance {oldInstance} --name web --root /repo/web --lang ts --globs \"*.ts,*.tsx\"");
            Assert.Equal(0, exitCode);
            string expected = stdout.Trim();

            // New: ProjectTool.
            GrimoiraCliRunner.Run($"init --instance {newInstance}");
            string dbPath = GrimoiraCliRunner.InstanceDbPath(newInstance);
            string actual;
            using (SqliteConnection connection = StoreConnection.Open(dbPath))
            {
                actual = new ProjectTool().Execute(connection, "web", "/repo/web", "ts", "*.ts,*.tsx").Trim();
            }

            Assert.Equal(expected, actual);
            Assert.Equal("project 'web' registered.", actual);

            using SqliteConnection check = new($"Data Source={dbPath};Mode=ReadOnly");
            check.Open();
            using SqliteCommand read = check.CreateCommand();
            read.CommandText = "SELECT root,lang,globs FROM projects WHERE name='web'";
            using SqliteDataReader reader = read.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal("/repo/web", reader.GetString(0));
            Assert.Equal("ts", reader.GetString(1));
            Assert.Equal("*.ts,*.tsx", reader.GetString(2));
        }
        finally
        {
            GrimoiraCliRunner.DeleteInstance(oldInstance);
            GrimoiraCliRunner.DeleteInstance(newInstance);
        }
    }

    [Fact]
    public void RegisteringTheSameNameTwiceUpdatesInPlace()
    {
        string instance = GrimoiraCliRunner.NewTestInstance("project-update");
        try
        {
            GrimoiraCliRunner.Run($"init --instance {instance}");
            string dbPath = GrimoiraCliRunner.InstanceDbPath(instance);
            using SqliteConnection connection = StoreConnection.Open(dbPath);

            new ProjectTool().Execute(connection, "web", "/repo/web", "ts", "*.ts");
            new ProjectTool().Execute(connection, "web", "/repo/web-v2", "ts", "*.ts,*.tsx");

            using SqliteCommand count = connection.CreateCommand();
            count.CommandText = "SELECT count(*) FROM projects";
            Assert.Equal(1L, (long)(count.ExecuteScalar() ?? 0L));

            using SqliteCommand read = connection.CreateCommand();
            read.CommandText = "SELECT root,globs FROM projects WHERE name='web'";
            using SqliteDataReader reader = read.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal("/repo/web-v2", reader.GetString(0));
            Assert.Equal("*.ts,*.tsx", reader.GetString(1));
        }
        finally
        {
            GrimoiraCliRunner.DeleteInstance(instance);
        }
    }

    // 2026-10-05: a workspace move re-registers a project under a new root. The index-code rows written
    // under the OLD root point at files that no longer exist there, so they go with the move. Curated
    // rows and other projects' rows stay.
    [Fact]
    public void MovingTheRootDropsTheOldRootsIndexedRows()
    {
        string instance = GrimoiraCliRunner.NewTestInstance("project-move-root");
        string oldRoot = IndexCodeStaleRowsTests.MakeFixtureProject("project-move-old");
        string newRoot = Path.Combine(Path.GetTempPath(), $"grimoira-project-move-new-{Guid.NewGuid():N}");
        string backupDir = Path.Combine(Path.GetTempPath(), $"grimoira-project-move-backups-{Guid.NewGuid():N}");
        try
        {
            using SqliteConnection connection = IndexCodeStaleRowsTests.OpenFreshStore(instance, backupDir);
            new ProjectTool().Execute(connection, "web", oldRoot, "", "*.ts,*.cs");
            new IndexCodeTool().Execute(connection, "web", backupDir);
            Assert.Contains("WidgetService", IndexCodeStaleRowsTests.Symbols(connection, "web"));
            IndexCodeStaleRowsTests.InsertEdge(connection, "CuratedSymbol", "usage", "web", oldRoot.Replace('\\', '/') + "/curated.ts", 3, "curated usage");
            IndexCodeStaleRowsTests.InsertEdge(connection, "OtherProjectSymbol", "decl", "other", "/repo/other/x.ts", 1, "ts declaration");

            Directory.Move(oldRoot, newRoot);
            new ProjectTool().Execute(connection, "web", newRoot, "", "*.ts,*.cs");

            List<string> web = IndexCodeStaleRowsTests.Symbols(connection, "web");
            Assert.DoesNotContain("WidgetService", web);
            Assert.Contains("CuratedSymbol", web);
            Assert.Contains("OtherProjectSymbol", IndexCodeStaleRowsTests.Symbols(connection, "other"));
        }
        finally
        {
            GrimoiraCliRunner.DeleteInstance(instance);
            if (Directory.Exists(oldRoot)) Directory.Delete(oldRoot, recursive: true);
            if (Directory.Exists(newRoot)) Directory.Delete(newRoot, recursive: true);
        }
    }
}
