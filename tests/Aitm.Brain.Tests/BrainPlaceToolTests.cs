using Aitm.Brain.Tools;
using Aitm.Store.Data;
using Aitm.TestSupport;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Aitm.Brain.Tests;

public class BrainPlaceToolTests
{
    [Fact]
    public void CliShapeMatchesTodaysCliOutputForAKnownKind()
    {
        string oldInstance = AitmCliRunner.NewTestInstance("brain-place-cli-old");
        string newInstance = AitmCliRunner.NewTestInstance("brain-place-cli-new");
        try
        {
            // Oracle: today's aitm.cs BrainPlace() (aitm.cs:1481).
            string oldDb = AitmCliRunner.InstanceDbPath(oldInstance);
            AitmCliRunner.Run($"init --instance {oldInstance}");
            SeedPlacement(oldDb);
            (string stdout, int exitCode) = AitmCliRunner.Run($"brain place --instance {oldInstance} vue-component");
            Assert.Equal(0, exitCode);
            string expected = stdout.Trim();

            string newDb = AitmCliRunner.InstanceDbPath(newInstance);
            AitmCliRunner.Run($"init --instance {newInstance}");
            SeedPlacement(newDb);

            using SqliteConnection connection = StoreConnection.Open(newDb);
            string actual = new BrainPlaceTool().ExecuteCli(connection, "vue-component").Trim();

            Assert.Equal(expected, actual);
            Assert.Contains("composition-api", actual);
        }
        finally
        {
            AitmCliRunner.DeleteInstance(oldInstance);
            AitmCliRunner.DeleteInstance(newInstance);
        }
    }

    [Fact]
    public void CliShapePrintsUsageWithNoCodekind()
    {
        string instance = AitmCliRunner.NewTestInstance("brain-place-cli-usage");
        try
        {
            AitmCliRunner.Run($"init --instance {instance}");

            (string stdout, int exitCode) = AitmCliRunner.Run($"brain place --instance {instance}");
            Assert.Equal(0, exitCode);
            string expected = stdout.Trim();

            string dbPath = AitmCliRunner.InstanceDbPath(instance);
            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = new BrainPlaceTool().ExecuteCli(connection, "").Trim();

            Assert.Equal(expected, actual);
            Assert.Equal("usage: brain place <codekind>", actual);
        }
        finally
        {
            AitmCliRunner.DeleteInstance(instance);
        }
    }

    [Fact]
    public void McpShapeMatchesTodaysMcpOutputForAKnownKind()
    {
        string instance = AitmCliRunner.NewTestInstance("brain-place-mcp");
        string? previousInstanceEnv = Environment.GetEnvironmentVariable("AITM_INSTANCE");
        try
        {
            string dbPath = AitmCliRunner.InstanceDbPath(instance);
            AitmCliRunner.Run($"init --instance {instance}");
            SeedPlacement(dbPath);

            Environment.SetEnvironmentVariable("AITM_INSTANCE", instance);
            string expected = (string)McpDll.Invoke("brain_place", "vue-component")!;

            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = new BrainPlaceTool().ExecuteMcp(connection, "vue-component");

            Assert.Equal(expected, actual);
            Assert.Contains("composition-api", actual);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AITM_INSTANCE", previousInstanceEnv);
            AitmCliRunner.DeleteInstance(instance);
        }
    }

    [Fact]
    public void McpShapeLogsAGapForAnUnknownKind()
    {
        string instance = AitmCliRunner.NewTestInstance("brain-place-mcp-unknown");
        string? previousInstanceEnv = Environment.GetEnvironmentVariable("AITM_INSTANCE");
        try
        {
            string dbPath = AitmCliRunner.InstanceDbPath(instance);
            AitmCliRunner.Run($"init --instance {instance}");

            Environment.SetEnvironmentVariable("AITM_INSTANCE", instance);
            string expected = (string)McpDll.Invoke("brain_place", "never-seen-codekind")!;

            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = new BrainPlaceTool().ExecuteMcp(connection, "never-seen-codekind");

            Assert.Equal(expected, actual);
            Assert.Equal("(nothing) [gap logged — stage the answer via brain_stage once you learn it]", actual);

            using SqliteCommand count = connection.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM gaps WHERE tool='brain_place'";
            Assert.Equal(1L, (long)count.ExecuteScalar()!);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AITM_INSTANCE", previousInstanceEnv);
            AitmCliRunner.DeleteInstance(instance);
        }
    }

    private static void SeedPlacement(string dbPath)
    {
        BrainTestFixtures.InsertNode(dbPath, "kind:vue-component", "codekind", "Vue Component", "A Vue SFC");
        BrainTestFixtures.InsertSlot(dbPath, "kind:vue-component", "style", "composition-api", "text", "team convention");
    }
}
