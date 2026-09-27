using Grimora.Brain.Tools;
using Grimora.Store.Data;
using Grimora.TestSupport;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Grimora.Brain.Tests;

public class BrainScopeToolTests
{
    [Fact]
    public void CliShapeMatchesTodaysCliOutputForSharedTopic()
    {
        string oldInstance = GrimoraCliRunner.NewTestInstance("brain-scope-cli-old");
        string newInstance = GrimoraCliRunner.NewTestInstance("brain-scope-cli-new");
        try
        {
            // Oracle: today's grimora.cs BrainScope() (grimora.cs:1433).
            string oldDb = GrimoraCliRunner.InstanceDbPath(oldInstance);
            GrimoraCliRunner.Run($"init --instance {oldInstance}");
            SeedSharedTopic(oldDb);
            (string stdout, int exitCode) = GrimoraCliRunner.Run($"brain scope --instance {oldInstance} proj:web proj:api");
            Assert.Equal(0, exitCode);
            string expected = stdout.Trim();

            string newDb = GrimoraCliRunner.InstanceDbPath(newInstance);
            GrimoraCliRunner.Run($"init --instance {newInstance}");
            SeedSharedTopic(newDb);

            using SqliteConnection connection = StoreConnection.Open(newDb);
            string actual = new BrainScopeTool().ExecuteCli(connection, ["proj:web", "proj:api"]).Trim();

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
    public void CliShapePrintsUsageWithNoProjects()
    {
        string instance = GrimoraCliRunner.NewTestInstance("brain-scope-cli-usage");
        try
        {
            GrimoraCliRunner.Run($"init --instance {instance}");

            (string stdout, int exitCode) = GrimoraCliRunner.Run($"brain scope --instance {instance}");
            Assert.Equal(0, exitCode);
            string expected = stdout.Trim();

            string dbPath = GrimoraCliRunner.InstanceDbPath(instance);
            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = new BrainScopeTool().ExecuteCli(connection, []).Trim();

            Assert.Equal(expected, actual);
            Assert.Equal("usage: brain scope <projectA> <projectB> [...]", actual);
        }
        finally
        {
            GrimoraCliRunner.DeleteInstance(instance);
        }
    }

    [Fact]
    public void McpShapeMatchesTodaysMcpOutputForSharedTopic()
    {
        string instance = GrimoraCliRunner.NewTestInstance("brain-scope-mcp");
        string? previousInstanceEnv = Environment.GetEnvironmentVariable("GRIMORA_INSTANCE");
        try
        {
            string dbPath = GrimoraCliRunner.InstanceDbPath(instance);
            GrimoraCliRunner.Run($"init --instance {instance}");
            SeedSharedTopic(dbPath);

            Environment.SetEnvironmentVariable("GRIMORA_INSTANCE", instance);
            string expected = (string)McpDll.Invoke("brain_scope", "proj:web proj:api")!;

            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = new BrainScopeTool().ExecuteMcp(connection, "proj:web proj:api");

            Assert.Equal(expected, actual);
            Assert.Contains("contract:paginated", actual);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GRIMORA_INSTANCE", previousInstanceEnv);
            GrimoraCliRunner.DeleteInstance(instance);
        }
    }

    [Fact]
    public void McpShapeReportsGiveOneProjectWhenNoneGiven()
    {
        string instance = GrimoraCliRunner.NewTestInstance("brain-scope-mcp-usage");
        string? previousInstanceEnv = Environment.GetEnvironmentVariable("GRIMORA_INSTANCE");
        try
        {
            string dbPath = GrimoraCliRunner.InstanceDbPath(instance);
            GrimoraCliRunner.Run($"init --instance {instance}");

            Environment.SetEnvironmentVariable("GRIMORA_INSTANCE", instance);
            string expected = (string)McpDll.Invoke("brain_scope", "")!;

            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = new BrainScopeTool().ExecuteMcp(connection, "");

            Assert.Equal(expected, actual);
            Assert.Equal("give 1+ project (e.g. 'server web').", actual);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GRIMORA_INSTANCE", previousInstanceEnv);
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
