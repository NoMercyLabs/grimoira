using Aitm.Brain.Tools;
using Aitm.Store.Data;
using Aitm.TestSupport;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Aitm.Brain.Tests;

public class BrainScopeToolTests
{
    [Fact]
    public void CliShapeMatchesTodaysCliOutputForSharedTopic()
    {
        string oldInstance = AitmCliRunner.NewTestInstance("brain-scope-cli-old");
        string newInstance = AitmCliRunner.NewTestInstance("brain-scope-cli-new");
        try
        {
            // Oracle: today's aitm.cs BrainScope() (aitm.cs:1433).
            string oldDb = AitmCliRunner.InstanceDbPath(oldInstance);
            AitmCliRunner.Run($"init --instance {oldInstance}");
            SeedSharedTopic(oldDb);
            (string stdout, int exitCode) = AitmCliRunner.Run($"brain scope --instance {oldInstance} proj:web proj:api");
            Assert.Equal(0, exitCode);
            string expected = stdout.Trim();

            string newDb = AitmCliRunner.InstanceDbPath(newInstance);
            AitmCliRunner.Run($"init --instance {newInstance}");
            SeedSharedTopic(newDb);

            using SqliteConnection connection = StoreConnection.Open(newDb);
            string actual = new BrainScopeTool().ExecuteCli(connection, ["proj:web", "proj:api"]).Trim();

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
    public void CliShapePrintsUsageWithNoProjects()
    {
        string instance = AitmCliRunner.NewTestInstance("brain-scope-cli-usage");
        try
        {
            AitmCliRunner.Run($"init --instance {instance}");

            (string stdout, int exitCode) = AitmCliRunner.Run($"brain scope --instance {instance}");
            Assert.Equal(0, exitCode);
            string expected = stdout.Trim();

            string dbPath = AitmCliRunner.InstanceDbPath(instance);
            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = new BrainScopeTool().ExecuteCli(connection, []).Trim();

            Assert.Equal(expected, actual);
            Assert.Equal("usage: brain scope <projectA> <projectB> [...]", actual);
        }
        finally
        {
            AitmCliRunner.DeleteInstance(instance);
        }
    }

    [Fact]
    public void McpShapeMatchesTodaysMcpOutputForSharedTopic()
    {
        string instance = AitmCliRunner.NewTestInstance("brain-scope-mcp");
        string? previousInstanceEnv = Environment.GetEnvironmentVariable("AITM_INSTANCE");
        try
        {
            string dbPath = AitmCliRunner.InstanceDbPath(instance);
            AitmCliRunner.Run($"init --instance {instance}");
            SeedSharedTopic(dbPath);

            Environment.SetEnvironmentVariable("AITM_INSTANCE", instance);
            string expected = (string)McpDll.Invoke("brain_scope", "proj:web proj:api")!;

            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = new BrainScopeTool().ExecuteMcp(connection, "proj:web proj:api");

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
    public void McpShapeReportsGiveOneProjectWhenNoneGiven()
    {
        string instance = AitmCliRunner.NewTestInstance("brain-scope-mcp-usage");
        string? previousInstanceEnv = Environment.GetEnvironmentVariable("AITM_INSTANCE");
        try
        {
            string dbPath = AitmCliRunner.InstanceDbPath(instance);
            AitmCliRunner.Run($"init --instance {instance}");

            Environment.SetEnvironmentVariable("AITM_INSTANCE", instance);
            string expected = (string)McpDll.Invoke("brain_scope", "")!;

            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = new BrainScopeTool().ExecuteMcp(connection, "");

            Assert.Equal(expected, actual);
            Assert.Equal("give 1+ project (e.g. 'server web').", actual);
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
