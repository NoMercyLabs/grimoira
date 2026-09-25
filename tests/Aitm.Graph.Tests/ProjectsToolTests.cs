using Aitm.Graph.Tools;
using Aitm.Store.Data;
using Aitm.TestSupport;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Aitm.Graph.Tests;

public class ProjectsToolTests
{
    [Fact]
    public void MatchesTodaysCliOutputAndListsEveryRegisteredProject()
    {
        string oldInstance = AitmCliRunner.NewTestInstance("projects-old");
        string newInstance = AitmCliRunner.NewTestInstance("projects-new");
        try
        {
            // Oracle: today's aitm.cs case "projects" -> ListProjects() (aitm.cs:2817-2825).
            AitmCliRunner.Run($"init --instance {oldInstance}");
            AitmCliRunner.Run($"project --instance {oldInstance} --name web --root /repo/web --lang ts --globs \"*.ts\"");
            AitmCliRunner.Run($"project --instance {oldInstance} --name api --root /repo/api --lang cs --globs \"*.cs\"");
            (string stdout, int exitCode) = AitmCliRunner.Run($"projects --instance {oldInstance}");
            Assert.Equal(0, exitCode);
            string expected = stdout.Trim();

            // New: ProjectsTool.
            AitmCliRunner.Run($"init --instance {newInstance}");
            string dbPath = AitmCliRunner.InstanceDbPath(newInstance);
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
            AitmCliRunner.DeleteInstance(oldInstance);
            AitmCliRunner.DeleteInstance(newInstance);
        }
    }

    [Fact]
    public void ReportsZeroProjectsForAFreshInstance()
    {
        string instance = AitmCliRunner.NewTestInstance("projects-empty");
        try
        {
            AitmCliRunner.Run($"init --instance {instance}");

            (string stdout, int exitCode) = AitmCliRunner.Run($"projects --instance {instance}");
            Assert.Equal(0, exitCode);
            string expected = stdout.Trim();

            string dbPath = AitmCliRunner.InstanceDbPath(instance);
            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = new ProjectsTool().Execute(connection).Trim();

            Assert.Equal(expected, actual);
            Assert.Equal("(0 project(s) registered)", actual);
        }
        finally
        {
            AitmCliRunner.DeleteInstance(instance);
        }
    }
}
