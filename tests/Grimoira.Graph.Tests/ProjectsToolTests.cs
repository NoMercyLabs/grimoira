using Grimoira.Graph.Tools;
using Grimoira.Store.Data;
using Grimoira.TestSupport;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Grimoira.Graph.Tests;

public class ProjectsToolTests
{
    [Fact]
    public void MatchesTodaysCliOutputAndListsEveryRegisteredProject()
    {
        string oldInstance = GrimoiraCliRunner.NewTestInstance("projects-old");
        string newInstance = GrimoiraCliRunner.NewTestInstance("projects-new");
        try
        {
            // Oracle: today's grimoira.cs case "projects" -> ListProjects() (grimoira.cs:2817-2825).
            GrimoiraCliRunner.Run($"init --instance {oldInstance}");
            GrimoiraCliRunner.Run($"project --instance {oldInstance} --name web --root /repo/web --lang ts --globs \"*.ts\"");
            GrimoiraCliRunner.Run($"project --instance {oldInstance} --name api --root /repo/api --lang cs --globs \"*.cs\"");
            (string stdout, int exitCode) = GrimoiraCliRunner.Run($"projects --instance {oldInstance}");
            Assert.Equal(0, exitCode);
            string expected = stdout.Trim();

            // New: ProjectsTool.
            GrimoiraCliRunner.Run($"init --instance {newInstance}");
            string dbPath = GrimoiraCliRunner.InstanceDbPath(newInstance);
            string actual;
            using (SqliteConnection connection = StoreConnection.Open(dbPath))
            {
                new ProjectTool().Execute(connection, "web", "/repo/web", "ts", "*.ts");
                new ProjectTool().Execute(connection, "api", "/repo/api", "cs", "*.cs");
                actual = new ProjectsTool().Execute(connection).Trim();
            }

            Assert.Equal(expected, actual);
            Assert.Contains("api", actual);
            Assert.Contains("web", actual);
            Assert.Contains("(2 project(s) registered)", actual);
        }
        finally
        {
            GrimoiraCliRunner.DeleteInstance(oldInstance);
            GrimoiraCliRunner.DeleteInstance(newInstance);
        }
    }

    [Fact]
    public void ReportsZeroProjectsForAFreshInstance()
    {
        string instance = GrimoiraCliRunner.NewTestInstance("projects-empty");
        try
        {
            GrimoiraCliRunner.Run($"init --instance {instance}");

            (string stdout, int exitCode) = GrimoiraCliRunner.Run($"projects --instance {instance}");
            Assert.Equal(0, exitCode);
            string expected = stdout.Trim();

            string dbPath = GrimoiraCliRunner.InstanceDbPath(instance);
            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = new ProjectsTool().Execute(connection).Trim();

            Assert.Equal(expected, actual);
            Assert.Equal("(0 project(s) registered)", actual);
        }
        finally
        {
            GrimoiraCliRunner.DeleteInstance(instance);
        }
    }
}
