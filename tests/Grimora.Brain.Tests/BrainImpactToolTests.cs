using Grimora.Brain.Tools;
using Grimora.Store.Data;
using Grimora.TestSupport;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Grimora.Brain.Tests;

public class BrainImpactToolTests
{
    [Fact]
    public void CliShapeMatchesTodaysCliOutputForAKnownConsumer()
    {
        string oldInstance = GrimoraCliRunner.NewTestInstance("brain-impact-cli-old");
        string newInstance = GrimoraCliRunner.NewTestInstance("brain-impact-cli-new");
        try
        {
            // Oracle: today's grimora.cs BrainImpact() (grimora.cs:1566).
            string oldDb = GrimoraCliRunner.InstanceDbPath(oldInstance);
            GrimoraCliRunner.Run($"init --instance {oldInstance}");
            SeedConsumer(oldDb);
            (string stdout, int exitCode) = GrimoraCliRunner.Run($"brain impact --instance {oldInstance} device_id");
            Assert.Equal(0, exitCode);
            string expected = stdout.Trim();

            string newDb = GrimoraCliRunner.InstanceDbPath(newInstance);
            GrimoraCliRunner.Run($"init --instance {newInstance}");
            SeedConsumer(newDb);

            using SqliteConnection connection = StoreConnection.Open(newDb);
            string actual = new BrainImpactTool().ExecuteCli(connection, "device_id").Trim();

            Assert.Equal(expected, actual);
            Assert.Contains("web", actual);
        }
        finally
        {
            GrimoraCliRunner.DeleteInstance(oldInstance);
            GrimoraCliRunner.DeleteInstance(newInstance);
        }
    }

    [Fact]
    public void CliShapePrintsUsageForEmptyTerm()
    {
        string instance = GrimoraCliRunner.NewTestInstance("brain-impact-cli-usage");
        try
        {
            GrimoraCliRunner.Run($"init --instance {instance}");

            (string stdout, int exitCode) = GrimoraCliRunner.Run($"brain impact --instance {instance}");
            Assert.Equal(0, exitCode);
            string expected = stdout.Trim();

            string dbPath = GrimoraCliRunner.InstanceDbPath(instance);
            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = new BrainImpactTool().ExecuteCli(connection, "").Trim();

            Assert.Equal(expected, actual);
            Assert.Equal("usage: brain impact <symbol-or-contract>", actual);
        }
        finally
        {
            GrimoraCliRunner.DeleteInstance(instance);
        }
    }

    [Fact]
    public void McpShapeMatchesTodaysMcpOutputForAKnownConsumer()
    {
        string instance = GrimoraCliRunner.NewTestInstance("brain-impact-mcp");
        string? previousInstanceEnv = Environment.GetEnvironmentVariable("Grimora_INSTANCE");
        try
        {
            string dbPath = GrimoraCliRunner.InstanceDbPath(instance);
            GrimoraCliRunner.Run($"init --instance {instance}");
            SeedConsumer(dbPath);

            Environment.SetEnvironmentVariable("Grimora_INSTANCE", instance);
            string expected = (string)McpDll.Invoke("brain_impact", "device_id")!;

            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = new BrainImpactTool().ExecuteMcp(connection, "device_id");

            Assert.Equal(expected, actual);
            Assert.Contains("web", actual);
        }
        finally
        {
            Environment.SetEnvironmentVariable("Grimora_INSTANCE", previousInstanceEnv);
            GrimoraCliRunner.DeleteInstance(instance);
        }
    }

    private static void SeedConsumer(string dbPath)
    {
        BrainTestFixtures.InsertNode(dbPath, "proj:web", "proj", "Web", "Web app");
        BrainTestFixtures.InsertNode(dbPath, "seam:device_id", "seam", "device_id", "shared device identifier field");
        BrainTestFixtures.InsertSharingTriple(dbPath, "proj:web", "seam:device_id");
    }
}
