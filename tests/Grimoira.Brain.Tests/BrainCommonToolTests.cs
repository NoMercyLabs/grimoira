using Grimoira.Brain.Tools;
using Grimoira.Store.Data;
using Grimoira.TestSupport;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Grimoira.Brain.Tests;

public class BrainCommonToolTests
{
    [Fact]
    public void CliShapeMatchesTodaysCliOutputForSharedTopic()
    {
        string oldInstance = GrimoiraCliRunner.NewTestInstance("brain-common-cli-old");
        string newInstance = GrimoiraCliRunner.NewTestInstance("brain-common-cli-new");
        try
        {
            // Oracle: today's grimoira.cs BrainCommon() (grimoira.cs:1456).
            string oldDb = GrimoiraCliRunner.InstanceDbPath(oldInstance);
            GrimoiraCliRunner.Run($"init --instance {oldInstance}");
            SeedSharedTopic(oldDb);
            (string stdout, int exitCode) = GrimoiraCliRunner.Run($"brain common --instance {oldInstance} proj:web proj:api");
            Assert.Equal(0, exitCode);
            string expected = stdout.Trim();

            string newDb = GrimoiraCliRunner.InstanceDbPath(newInstance);
            GrimoiraCliRunner.Run($"init --instance {newInstance}");
            SeedSharedTopic(newDb);

            using SqliteConnection connection = StoreConnection.Open(newDb);
            string actual = new BrainCommonTool().ExecuteCli(connection, ["proj:web", "proj:api"]).Trim();

            Assert.Equal(expected, actual);
            Assert.Contains("contract:paginated", actual);
        }
        finally
        {
            GrimoiraCliRunner.DeleteInstance(oldInstance);
            GrimoiraCliRunner.DeleteInstance(newInstance);
        }
    }

    [Fact]
    public void CliShapePrintsUsageWithOneProject()
    {
        string instance = GrimoiraCliRunner.NewTestInstance("brain-common-cli-usage");
        try
        {
            GrimoiraCliRunner.Run($"init --instance {instance}");

            (string stdout, int exitCode) = GrimoiraCliRunner.Run($"brain common --instance {instance} proj:web");
            Assert.Equal(0, exitCode);
            string expected = stdout.Trim();

            string dbPath = GrimoiraCliRunner.InstanceDbPath(instance);
            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = new BrainCommonTool().ExecuteCli(connection, ["proj:web"]).Trim();

            Assert.Equal(expected, actual);
            Assert.Equal("usage: brain common <projA> <projB> [projC ...]", actual);
        }
        finally
        {
            GrimoiraCliRunner.DeleteInstance(instance);
        }
    }

    [Fact]
    public void McpShapeMatchesTodaysMcpOutputForSharedTopic()
    {
        string instance = GrimoiraCliRunner.NewTestInstance("brain-common-mcp");
        string? previousInstanceEnv = Environment.GetEnvironmentVariable("GRIMOIRA_INSTANCE");
        try
        {
            string dbPath = GrimoiraCliRunner.InstanceDbPath(instance);
            GrimoiraCliRunner.Run($"init --instance {instance}");
            SeedSharedTopic(dbPath);

            Environment.SetEnvironmentVariable("GRIMOIRA_INSTANCE", instance);
            string expected = (string)McpDll.Invoke("brain_common", "proj:web proj:api")!;

            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = new BrainCommonTool().ExecuteMcp(connection, "proj:web proj:api");

            Assert.Equal(expected, actual);
            Assert.Contains("contract:paginated", actual);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GRIMOIRA_INSTANCE", previousInstanceEnv);
            GrimoiraCliRunner.DeleteInstance(instance);
        }
    }

    [Fact]
    public void McpShapeReportsGiveTwoProjectsWithOneGiven()
    {
        string instance = GrimoiraCliRunner.NewTestInstance("brain-common-mcp-usage");
        string? previousInstanceEnv = Environment.GetEnvironmentVariable("GRIMOIRA_INSTANCE");
        try
        {
            string dbPath = GrimoiraCliRunner.InstanceDbPath(instance);
            GrimoiraCliRunner.Run($"init --instance {instance}");

            Environment.SetEnvironmentVariable("GRIMOIRA_INSTANCE", instance);
            string expected = (string)McpDll.Invoke("brain_common", "proj:web")!;

            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = new BrainCommonTool().ExecuteMcp(connection, "proj:web");

            Assert.Equal(expected, actual);
            Assert.Equal("give 2+ projects (e.g. 'web android ios').", actual);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GRIMOIRA_INSTANCE", previousInstanceEnv);
            GrimoiraCliRunner.DeleteInstance(instance);
        }
    }

    private static void SeedSharedTopic(string dbPath)
    {
        BrainTestFixtures.InsertNode(dbPath, "proj:web", "proj", "Web", "Web app");
        BrainTestFixtures.InsertNode(dbPath, "proj:api", "proj", "API", "Server API");
        BrainTestFixtures.InsertNode(dbPath, "contract:paginated", "contract", "PaginatedResponse", "Shared paging shape");
        BrainTestFixtures.InsertSharingTriple(dbPath, "proj:web", "contract:paginated");
        BrainTestFixtures.InsertSharingTriple(dbPath, "proj:api", "contract:paginated");
    }
}
