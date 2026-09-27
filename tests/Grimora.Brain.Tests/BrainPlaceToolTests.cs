using Grimora.Brain.Tools;
using Grimora.Store.Data;
using Grimora.TestSupport;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Grimora.Brain.Tests;

public class BrainPlaceToolTests
{
    [Fact]
    public void CliShapeMatchesTodaysCliOutputForAKnownKind()
    {
        string oldInstance = GrimoraCliRunner.NewTestInstance("brain-place-cli-old");
        string newInstance = GrimoraCliRunner.NewTestInstance("brain-place-cli-new");
        try
        {
            // Oracle: today's grimora.cs BrainPlace() (grimora.cs:1481).
            string oldDb = GrimoraCliRunner.InstanceDbPath(oldInstance);
            GrimoraCliRunner.Run($"init --instance {oldInstance}");
            SeedPlacement(oldDb);
            (string stdout, int exitCode) = GrimoraCliRunner.Run($"brain place --instance {oldInstance} vue-component");
            Assert.Equal(0, exitCode);
            string expected = stdout.Trim();

            string newDb = GrimoraCliRunner.InstanceDbPath(newInstance);
            GrimoraCliRunner.Run($"init --instance {newInstance}");
            SeedPlacement(newDb);

            using SqliteConnection connection = StoreConnection.Open(newDb);
            string actual = new BrainPlaceTool().ExecuteCli(connection, "vue-component").Trim();

            Assert.Equal(expected, actual);
            Assert.Contains("composition-api", actual);
        }
        finally
        {
            GrimoraCliRunner.DeleteInstance(oldInstance);
            GrimoraCliRunner.DeleteInstance(newInstance);
        }
    }

    [Fact]
    public void CliShapePrintsUsageWithNoCodekind()
    {
        string instance = GrimoraCliRunner.NewTestInstance("brain-place-cli-usage");
        try
        {
            GrimoraCliRunner.Run($"init --instance {instance}");

            (string stdout, int exitCode) = GrimoraCliRunner.Run($"brain place --instance {instance}");
            Assert.Equal(0, exitCode);
            string expected = stdout.Trim();

            string dbPath = GrimoraCliRunner.InstanceDbPath(instance);
            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = new BrainPlaceTool().ExecuteCli(connection, "").Trim();

            Assert.Equal(expected, actual);
            Assert.Equal("usage: brain place <codekind>", actual);
        }
        finally
        {
            GrimoraCliRunner.DeleteInstance(instance);
        }
    }

    [Fact]
    public void McpShapeMatchesTodaysMcpOutputForAKnownKind()
    {
        string instance = GrimoraCliRunner.NewTestInstance("brain-place-mcp");
        string? previousInstanceEnv = Environment.GetEnvironmentVariable("Grimora_INSTANCE");
        try
        {
            string dbPath = GrimoraCliRunner.InstanceDbPath(instance);
            GrimoraCliRunner.Run($"init --instance {instance}");
            SeedPlacement(dbPath);

            Environment.SetEnvironmentVariable("Grimora_INSTANCE", instance);
            string expected = (string)McpDll.Invoke("brain_place", "vue-component")!;

            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = new BrainPlaceTool().ExecuteMcp(connection, "vue-component");

            Assert.Equal(expected, actual);
            Assert.Contains("composition-api", actual);
        }
        finally
        {
            Environment.SetEnvironmentVariable("Grimora_INSTANCE", previousInstanceEnv);
            GrimoraCliRunner.DeleteInstance(instance);
        }
    }

    [Fact]
    public void McpShapeLogsAGapForAnUnknownKind()
    {
        string instance = GrimoraCliRunner.NewTestInstance("brain-place-mcp-unknown");
        string? previousInstanceEnv = Environment.GetEnvironmentVariable("Grimora_INSTANCE");
        try
        {
            string dbPath = GrimoraCliRunner.InstanceDbPath(instance);
            GrimoraCliRunner.Run($"init --instance {instance}");

            Environment.SetEnvironmentVariable("Grimora_INSTANCE", instance);
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
            Environment.SetEnvironmentVariable("Grimora_INSTANCE", previousInstanceEnv);
            GrimoraCliRunner.DeleteInstance(instance);
        }
    }

    private static void SeedPlacement(string dbPath)
    {
        BrainTestFixtures.InsertNode(dbPath, "kind:vue-component", "codekind", "Vue Component", "A Vue SFC");
        BrainTestFixtures.InsertSlot(dbPath, "kind:vue-component", "style", "composition-api", "text", "team convention");
    }
}
