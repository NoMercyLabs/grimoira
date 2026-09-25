using Aitm.Brain.Tools;
using Aitm.Store.Data;
using Aitm.TestSupport;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Aitm.Brain.Tests;

public class BrainImpactToolTests
{
    [Fact]
    public void CliShapeMatchesTodaysCliOutputForAKnownConsumer()
    {
        string oldInstance = AitmCliRunner.NewTestInstance("brain-impact-cli-old");
        string newInstance = AitmCliRunner.NewTestInstance("brain-impact-cli-new");
        try
        {
            // Oracle: today's aitm.cs BrainImpact() (aitm.cs:1566).
            string oldDb = AitmCliRunner.InstanceDbPath(oldInstance);
            AitmCliRunner.Run($"init --instance {oldInstance}");
            SeedConsumer(oldDb);
            (string stdout, int exitCode) = AitmCliRunner.Run($"brain impact --instance {oldInstance} device_id");
            Assert.Equal(0, exitCode);
            string expected = stdout.Trim();

            string newDb = AitmCliRunner.InstanceDbPath(newInstance);
            AitmCliRunner.Run($"init --instance {newInstance}");
            SeedConsumer(newDb);

            using SqliteConnection connection = StoreConnection.Open(newDb);
            string actual = new BrainImpactTool().ExecuteCli(connection, "device_id").Trim();

            Assert.Equal(expected, actual);
            Assert.Contains("web", actual);
        }
        finally
        {
            AitmCliRunner.DeleteInstance(oldInstance);
            AitmCliRunner.DeleteInstance(newInstance);
        }
    }

    [Fact]
    public void CliShapePrintsUsageForEmptyTerm()
    {
        string instance = AitmCliRunner.NewTestInstance("brain-impact-cli-usage");
        try
        {
            AitmCliRunner.Run($"init --instance {instance}");

            (string stdout, int exitCode) = AitmCliRunner.Run($"brain impact --instance {instance}");
            Assert.Equal(0, exitCode);
            string expected = stdout.Trim();

            string dbPath = AitmCliRunner.InstanceDbPath(instance);
            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = new BrainImpactTool().ExecuteCli(connection, "").Trim();

            Assert.Equal(expected, actual);
            Assert.Equal("usage: brain impact <symbol-or-contract>", actual);
        }
        finally
        {
            AitmCliRunner.DeleteInstance(instance);
        }
    }

    [Fact]
    public void McpShapeMatchesTodaysMcpOutputForAKnownConsumer()
    {
        string instance = AitmCliRunner.NewTestInstance("brain-impact-mcp");
        string? previousInstanceEnv = Environment.GetEnvironmentVariable("AITM_INSTANCE");
        try
        {
            string dbPath = AitmCliRunner.InstanceDbPath(instance);
            AitmCliRunner.Run($"init --instance {instance}");
            SeedConsumer(dbPath);

            Environment.SetEnvironmentVariable("AITM_INSTANCE", instance);
            string expected = (string)McpDll.Invoke("brain_impact", "device_id")!;

            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = new BrainImpactTool().ExecuteMcp(connection, "device_id");

            Assert.Equal(expected, actual);
            Assert.Contains("web", actual);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AITM_INSTANCE", previousInstanceEnv);
            AitmCliRunner.DeleteInstance(instance);
        }
    }

    private static void SeedConsumer(string dbPath)
    {
        BrainTestFixtures.InsertNode(dbPath, "proj:web", "proj", "Web", "Web app");
        BrainTestFixtures.InsertNode(dbPath, "seam:device_id", "seam", "device_id", "shared device identifier field");
        BrainTestFixtures.InsertSharingTriple(dbPath, "proj:web", "seam:device_id");
    }
}
