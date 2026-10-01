using Grimoira.Brain.Tools;
using Grimoira.Store.Data;
using Grimoira.TestSupport;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Grimoira.Brain.Tests;

public class BrainImpactToolTests
{
    [Fact]
    public void CliShapeMatchesTodaysCliOutputForAKnownConsumer()
    {
        string oldInstance = GrimoiraCliRunner.NewTestInstance("brain-impact-cli-old");
        string newInstance = GrimoiraCliRunner.NewTestInstance("brain-impact-cli-new");
        try
        {
            // Oracle: today's grimoira.cs BrainImpact() (grimoira.cs:1566).
            string oldDb = GrimoiraCliRunner.InstanceDbPath(oldInstance);
            GrimoiraCliRunner.Run($"init --instance {oldInstance}");
            SeedConsumer(oldDb);
            (string stdout, int exitCode) = GrimoiraCliRunner.Run($"brain impact --instance {oldInstance} device_id");
            Assert.Equal(0, exitCode);
            string expected = stdout.Trim();

            string newDb = GrimoiraCliRunner.InstanceDbPath(newInstance);
            GrimoiraCliRunner.Run($"init --instance {newInstance}");
            SeedConsumer(newDb);

            using SqliteConnection connection = StoreConnection.Open(newDb);
            string actual = new BrainImpactTool().ExecuteCli(connection, "device_id").Trim();

            Assert.Equal(expected, actual);
            Assert.Contains("web", actual);
        }
        finally
        {
            GrimoiraCliRunner.DeleteInstance(oldInstance);
            GrimoiraCliRunner.DeleteInstance(newInstance);
        }
    }

    [Fact]
    public void CliShapePrintsUsageForEmptyTerm()
    {
        string instance = GrimoiraCliRunner.NewTestInstance("brain-impact-cli-usage");
        try
        {
            GrimoiraCliRunner.Run($"init --instance {instance}");

            (string stdout, int exitCode) = GrimoiraCliRunner.Run($"brain impact --instance {instance}");
            Assert.Equal(0, exitCode);
            string expected = stdout.Trim();

            string dbPath = GrimoiraCliRunner.InstanceDbPath(instance);
            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = new BrainImpactTool().ExecuteCli(connection, "").Trim();

            Assert.Equal(expected, actual);
            Assert.Equal("usage: brain impact <symbol-or-contract>", actual);
        }
        finally
        {
            GrimoiraCliRunner.DeleteInstance(instance);
        }
    }

    [Fact]
    public void McpShapeMatchesTodaysMcpOutputForAKnownConsumer()
    {
        string instance = GrimoiraCliRunner.NewTestInstance("brain-impact-mcp");
        string? previousInstanceEnv = Environment.GetEnvironmentVariable("GRIMOIRA_INSTANCE");
        try
        {
            string dbPath = GrimoiraCliRunner.InstanceDbPath(instance);
            GrimoiraCliRunner.Run($"init --instance {instance}");
            SeedConsumer(dbPath);

            Environment.SetEnvironmentVariable("GRIMOIRA_INSTANCE", instance);
            string expected = (string)McpDll.Invoke("brain_impact", "device_id")!;

            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = new BrainImpactTool().ExecuteMcp(connection, "device_id");

            Assert.Equal(expected, actual);
            Assert.Contains("web", actual);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GRIMOIRA_INSTANCE", previousInstanceEnv);
            GrimoiraCliRunner.DeleteInstance(instance);
        }
    }

    private static void SeedConsumer(string dbPath)
    {
        BrainTestFixtures.InsertNode(dbPath, "proj:web", "proj", "Web", "Web app");
        BrainTestFixtures.InsertNode(dbPath, "seam:device_id", "seam", "device_id", "shared device identifier field");
        BrainTestFixtures.InsertSharingTriple(dbPath, "proj:web", "seam:device_id");
    }
}
