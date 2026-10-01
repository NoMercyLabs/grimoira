using Grimoira.Brain.Tools;
using Grimoira.Store.Data;
using Grimoira.TestSupport;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Grimoira.Brain.Tests;

public class BrainPlaceToolTests
{
    [Fact]
    public void CliShapeMatchesTodaysCliOutputForAKnownKind()
    {
        string oldInstance = GrimoiraCliRunner.NewTestInstance("brain-place-cli-old");
        string newInstance = GrimoiraCliRunner.NewTestInstance("brain-place-cli-new");
        try
        {
            // Oracle: today's grimoira.cs BrainPlace() (grimoira.cs:1481).
            string oldDb = GrimoiraCliRunner.InstanceDbPath(oldInstance);
            GrimoiraCliRunner.Run($"init --instance {oldInstance}");
            SeedPlacement(oldDb);
            (string stdout, int exitCode) = GrimoiraCliRunner.Run($"brain place --instance {oldInstance} vue-component");
            Assert.Equal(0, exitCode);
            string expected = stdout.Trim();

            string newDb = GrimoiraCliRunner.InstanceDbPath(newInstance);
            GrimoiraCliRunner.Run($"init --instance {newInstance}");
            SeedPlacement(newDb);

            using SqliteConnection connection = StoreConnection.Open(newDb);
            string actual = new BrainPlaceTool().ExecuteCli(connection, "vue-component").Trim();

            Assert.Equal(expected, actual);
            Assert.Contains("composition-api", actual);
        }
        finally
        {
            GrimoiraCliRunner.DeleteInstance(oldInstance);
            GrimoiraCliRunner.DeleteInstance(newInstance);
        }
    }

    [Fact]
    public void CliShapePrintsUsageWithNoCodekind()
    {
        string instance = GrimoiraCliRunner.NewTestInstance("brain-place-cli-usage");
        try
        {
            GrimoiraCliRunner.Run($"init --instance {instance}");

            (string stdout, int exitCode) = GrimoiraCliRunner.Run($"brain place --instance {instance}");
            Assert.Equal(0, exitCode);
            string expected = stdout.Trim();

            string dbPath = GrimoiraCliRunner.InstanceDbPath(instance);
            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = new BrainPlaceTool().ExecuteCli(connection, "").Trim();

            Assert.Equal(expected, actual);
            Assert.Equal("usage: brain place <codekind>", actual);
        }
        finally
        {
            GrimoiraCliRunner.DeleteInstance(instance);
        }
    }

    [Fact]
    public void McpShapeMatchesTodaysMcpOutputForAKnownKind()
    {
        string instance = GrimoiraCliRunner.NewTestInstance("brain-place-mcp");
        string? previousInstanceEnv = Environment.GetEnvironmentVariable("GRIMOIRA_INSTANCE");
        try
        {
            string dbPath = GrimoiraCliRunner.InstanceDbPath(instance);
            GrimoiraCliRunner.Run($"init --instance {instance}");
            SeedPlacement(dbPath);

            Environment.SetEnvironmentVariable("GRIMOIRA_INSTANCE", instance);
            string expected = (string)McpDll.Invoke("brain_place", "vue-component")!;

            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = new BrainPlaceTool().ExecuteMcp(connection, "vue-component");

            Assert.Equal(expected, actual);
            Assert.Contains("composition-api", actual);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GRIMOIRA_INSTANCE", previousInstanceEnv);
            GrimoiraCliRunner.DeleteInstance(instance);
        }
    }

    [Fact]
    public void McpShapeLogsAGapForAnUnknownKind()
    {
        string instance = GrimoiraCliRunner.NewTestInstance("brain-place-mcp-unknown");
        string? previousInstanceEnv = Environment.GetEnvironmentVariable("GRIMOIRA_INSTANCE");
        try
        {
            string dbPath = GrimoiraCliRunner.InstanceDbPath(instance);
            GrimoiraCliRunner.Run($"init --instance {instance}");

            Environment.SetEnvironmentVariable("GRIMOIRA_INSTANCE", instance);
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
            Environment.SetEnvironmentVariable("GRIMOIRA_INSTANCE", previousInstanceEnv);
            GrimoiraCliRunner.DeleteInstance(instance);
        }
    }

    private static void SeedPlacement(string dbPath)
    {
        BrainTestFixtures.InsertNode(dbPath, "kind:vue-component", "codekind", "Vue Component", "A Vue SFC");
        BrainTestFixtures.InsertSlot(dbPath, "kind:vue-component", "style", "composition-api", "text", "team convention");
    }
}
