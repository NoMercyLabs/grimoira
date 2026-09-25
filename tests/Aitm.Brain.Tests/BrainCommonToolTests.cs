using Aitm.Brain.Tools;
using Aitm.Store.Data;
using Aitm.TestSupport;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Aitm.Brain.Tests;

public class BrainCommonToolTests
{
    [Fact]
    public void CliShapeMatchesTodaysCliOutputForSharedTopic()
    {
        string oldInstance = AitmCliRunner.NewTestInstance("brain-common-cli-old");
        string newInstance = AitmCliRunner.NewTestInstance("brain-common-cli-new");
        try
        {
            // Oracle: today's aitm.cs BrainCommon() (aitm.cs:1456).
            string oldDb = AitmCliRunner.InstanceDbPath(oldInstance);
            AitmCliRunner.Run($"init --instance {oldInstance}");
            SeedSharedTopic(oldDb);
            (string stdout, int exitCode) = AitmCliRunner.Run($"brain common --instance {oldInstance} proj:web proj:api");
            Assert.Equal(0, exitCode);
            string expected = stdout.Trim();

            string newDb = AitmCliRunner.InstanceDbPath(newInstance);
            AitmCliRunner.Run($"init --instance {newInstance}");
            SeedSharedTopic(newDb);

            using SqliteConnection connection = StoreConnection.Open(newDb);
            string actual = new BrainCommonTool().ExecuteCli(connection, ["proj:web", "proj:api"]).Trim();

            Assert.Equal(expected, actual);
            Assert.Contains("contract:paginated", actual);
        }
        finally
        {
            AitmCliRunner.DeleteInstance(oldInstance);
            AitmCliRunner.DeleteInstance(newInstance);
        }
    }

    [Fact]
    public void CliShapePrintsUsageWithOneProject()
    {
        string instance = AitmCliRunner.NewTestInstance("brain-common-cli-usage");
        try
        {
            AitmCliRunner.Run($"init --instance {instance}");

            (string stdout, int exitCode) = AitmCliRunner.Run($"brain common --instance {instance} proj:web");
            Assert.Equal(0, exitCode);
            string expected = stdout.Trim();

            string dbPath = AitmCliRunner.InstanceDbPath(instance);
            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = new BrainCommonTool().ExecuteCli(connection, ["proj:web"]).Trim();

            Assert.Equal(expected, actual);
            Assert.Equal("usage: brain common <projA> <projB> [projC ...]", actual);
        }
        finally
        {
            AitmCliRunner.DeleteInstance(instance);
        }
    }

    [Fact]
    public void McpShapeMatchesTodaysMcpOutputForSharedTopic()
    {
        string instance = AitmCliRunner.NewTestInstance("brain-common-mcp");
        string? previousInstanceEnv = Environment.GetEnvironmentVariable("AITM_INSTANCE");
        try
        {
            string dbPath = AitmCliRunner.InstanceDbPath(instance);
            AitmCliRunner.Run($"init --instance {instance}");
            SeedSharedTopic(dbPath);

            Environment.SetEnvironmentVariable("AITM_INSTANCE", instance);
            string expected = (string)McpDll.Invoke("brain_common", "proj:web proj:api")!;

            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = new BrainCommonTool().ExecuteMcp(connection, "proj:web proj:api");

            Assert.Equal(expected, actual);
            Assert.Contains("contract:paginated", actual);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AITM_INSTANCE", previousInstanceEnv);
            AitmCliRunner.DeleteInstance(instance);
        }
    }

    [Fact]
    public void McpShapeReportsGiveTwoProjectsWithOneGiven()
    {
        string instance = AitmCliRunner.NewTestInstance("brain-common-mcp-usage");
        string? previousInstanceEnv = Environment.GetEnvironmentVariable("AITM_INSTANCE");
        try
        {
            string dbPath = AitmCliRunner.InstanceDbPath(instance);
            AitmCliRunner.Run($"init --instance {instance}");

            Environment.SetEnvironmentVariable("AITM_INSTANCE", instance);
            string expected = (string)McpDll.Invoke("brain_common", "proj:web")!;

            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = new BrainCommonTool().ExecuteMcp(connection, "proj:web");

            Assert.Equal(expected, actual);
            Assert.Equal("give 2+ projects (e.g. 'web android ios').", actual);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AITM_INSTANCE", previousInstanceEnv);
            AitmCliRunner.DeleteInstance(instance);
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
