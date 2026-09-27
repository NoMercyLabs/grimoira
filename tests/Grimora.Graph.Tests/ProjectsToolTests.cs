using Grimora.Graph.Tools;
using Grimora.Store.Data;
using Grimora.TestSupport;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Grimora.Graph.Tests;

public class ProjectsToolTests
{
    [Fact]
    public void MatchesTodaysCliOutputAndListsEveryRegisteredProject()
    {
        string oldInstance = GrimoraCliRunner.NewTestInstance("projects-old");
        string newInstance = GrimoraCliRunner.NewTestInstance("projects-new");
        try
        {
            // Oracle: today's grimora.cs case "projects" -> ListProjects() (grimora.cs:2817-2825).
            GrimoraCliRunner.Run($"init --instance {oldInstance}");
            GrimoraCliRunner.Run($"project --instance {oldInstance} --name web --root /repo/web --lang ts --globs \"*.ts\"");
            GrimoraCliRunner.Run($"project --instance {oldInstance} --name api --root /repo/api --lang cs --globs \"*.cs\"");
            (string stdout, int exitCode) = GrimoraCliRunner.Run($"projects --instance {oldInstance}");
            Assert.Equal(0, exitCode);
            string expected = stdout.Trim();

            // New: ProjectsTool.
            GrimoraCliRunner.Run($"init --instance {newInstance}");
            string dbPath = GrimoraCliRunner.InstanceDbPath(newInstance);
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
            GrimoraCliRunner.DeleteInstance(oldInstance);
            GrimoraCliRunner.DeleteInstance(newInstance);
        }
    }

    [Fact]
    public void ReportsZeroProjectsForAFreshInstance()
    {
        string instance = GrimoraCliRunner.NewTestInstance("projects-empty");
        try
        {
            GrimoraCliRunner.Run($"init --instance {instance}");

            (string stdout, int exitCode) = GrimoraCliRunner.Run($"projects --instance {instance}");
            Assert.Equal(0, exitCode);
            string expected = stdout.Trim();

            string dbPath = GrimoraCliRunner.InstanceDbPath(instance);
            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = new ProjectsTool().Execute(connection).Trim();

            Assert.Equal(expected, actual);
            Assert.Equal("(0 project(s) registered)", actual);
        }
        finally
        {
            GrimoraCliRunner.DeleteInstance(instance);
        }
    }
}
