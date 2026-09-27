using Grimora.Brain.Tools;
using Grimora.Store.Data;
using Grimora.TestSupport;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Grimora.Brain.Tests;

public class BrainCommonToolTests
{
    [Fact]
    public void CliShapeMatchesTodaysCliOutputForSharedTopic()
    {
        string oldInstance = GrimoraCliRunner.NewTestInstance("brain-common-cli-old");
        string newInstance = GrimoraCliRunner.NewTestInstance("brain-common-cli-new");
        try
        {
            // Oracle: today's grimora.cs BrainCommon() (grimora.cs:1456).
            string oldDb = GrimoraCliRunner.InstanceDbPath(oldInstance);
            GrimoraCliRunner.Run($"init --instance {oldInstance}");
            SeedSharedTopic(oldDb);
            (string stdout, int exitCode) = GrimoraCliRunner.Run($"brain common --instance {oldInstance} proj:web proj:api");
            Assert.Equal(0, exitCode);
            string expected = stdout.Trim();

            string newDb = GrimoraCliRunner.InstanceDbPath(newInstance);
            GrimoraCliRunner.Run($"init --instance {newInstance}");
            SeedSharedTopic(newDb);

            using SqliteConnection connection = StoreConnection.Open(newDb);
            string actual = new BrainCommonTool().ExecuteCli(connection, ["proj:web", "proj:api"]).Trim();

            Assert.Equal(expected, actual);
            Assert.Contains("contract:paginated", actual);
        }
        finally
        {
            GrimoraCliRunner.DeleteInstance(oldInstance);
            GrimoraCliRunner.DeleteInstance(newInstance);
        }
    }

    [Fact]
    public void CliShapePrintsUsageWithOneProject()
    {
        string instance = GrimoraCliRunner.NewTestInstance("brain-common-cli-usage");
        try
        {
            GrimoraCliRunner.Run($"init --instance {instance}");

            (string stdout, int exitCode) = GrimoraCliRunner.Run($"brain common --instance {instance} proj:web");
            Assert.Equal(0, exitCode);
            string expected = stdout.Trim();

            string dbPath = GrimoraCliRunner.InstanceDbPath(instance);
            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = new BrainCommonTool().ExecuteCli(connection, ["proj:web"]).Trim();

            Assert.Equal(expected, actual);
            Assert.Equal("usage: brain common <projA> <projB> [projC ...]", actual);
        }
        finally
        {
            GrimoraCliRunner.DeleteInstance(instance);
        }
    }

    [Fact]
    public void McpShapeMatchesTodaysMcpOutputForSharedTopic()
    {
        string instance = GrimoraCliRunner.NewTestInstance("brain-common-mcp");
        string? previousInstanceEnv = Environment.GetEnvironmentVariable("Grimora_INSTANCE");
        try
        {
            string dbPath = GrimoraCliRunner.InstanceDbPath(instance);
            GrimoraCliRunner.Run($"init --instance {instance}");
            SeedSharedTopic(dbPath);

            Environment.SetEnvironmentVariable("Grimora_INSTANCE", instance);
            string expected = (string)McpDll.Invoke("brain_common", "proj:web proj:api")!;

            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = new BrainCommonTool().ExecuteMcp(connection, "proj:web proj:api");

            Assert.Equal(expected, actual);
            Assert.Contains("contract:paginated", actual);
        }
        finally
        {
            Environment.SetEnvironmentVariable("Grimora_INSTANCE", previousInstanceEnv);
            GrimoraCliRunner.DeleteInstance(instance);
        }
    }

    [Fact]
    public void McpShapeReportsGiveTwoProjectsWithOneGiven()
    {
        string instance = GrimoraCliRunner.NewTestInstance("brain-common-mcp-usage");
        string? previousInstanceEnv = Environment.GetEnvironmentVariable("Grimora_INSTANCE");
        try
        {
            string dbPath = GrimoraCliRunner.InstanceDbPath(instance);
            GrimoraCliRunner.Run($"init --instance {instance}");

            Environment.SetEnvironmentVariable("Grimora_INSTANCE", instance);
            string expected = (string)McpDll.Invoke("brain_common", "proj:web")!;

            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = new BrainCommonTool().ExecuteMcp(connection, "proj:web");

            Assert.Equal(expected, actual);
            Assert.Equal("give 2+ projects (e.g. 'web android ios').", actual);
        }
        finally
        {
            Environment.SetEnvironmentVariable("Grimora_INSTANCE", previousInstanceEnv);
            GrimoraCliRunner.DeleteInstance(instance);
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
