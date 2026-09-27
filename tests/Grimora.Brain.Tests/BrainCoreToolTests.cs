using Grimora.Brain.Tools;
using Grimora.Store.Data;
using Grimora.TestSupport;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Grimora.Brain.Tests;

public class BrainCoreToolTests
{
    [Fact]
    public void CliShapeMatchesTodaysCliOutputForHardNodes()
    {
        string oldInstance = GrimoraCliRunner.NewTestInstance("brain-core-cli-old");
        string newInstance = GrimoraCliRunner.NewTestInstance("brain-core-cli-new");
        try
        {
            // Oracle: today's grimora.cs BrainCore() (grimora.cs:1423).
            string oldDb = GrimoraCliRunner.InstanceDbPath(oldInstance);
            GrimoraCliRunner.Run($"init --instance {oldInstance}");
            BrainTestFixtures.InsertHardNode(oldDb, "rule:no-secrets", "rule", "No secrets in logs", "Never log a token.");
            (string stdout, int exitCode) = GrimoraCliRunner.Run($"brain core --instance {oldInstance}");
            Assert.Equal(0, exitCode);
            string expected = stdout.Trim();

            string newDb = GrimoraCliRunner.InstanceDbPath(newInstance);
            GrimoraCliRunner.Run($"init --instance {newInstance}");
            BrainTestFixtures.InsertHardNode(newDb, "rule:no-secrets", "rule", "No secrets in logs", "Never log a token.");

            using SqliteConnection connection = StoreConnection.Open(newDb);
            string actual = new BrainCoreTool().ExecuteCli(connection).Trim();

            Assert.Equal(expected, actual);
            Assert.Contains("rule:no-secrets", actual);
        }
        finally
        {
            GrimoraCliRunner.DeleteInstance(oldInstance);
            GrimoraCliRunner.DeleteInstance(newInstance);
        }
    }

    [Fact]
    public void McpShapeMatchesTodaysMcpOutputForHardNodes()
    {
        string instance = GrimoraCliRunner.NewTestInstance("brain-core-mcp");
        string? previousInstanceEnv = Environment.GetEnvironmentVariable("GRIMORA_INSTANCE");
        try
        {
            string dbPath = GrimoraCliRunner.InstanceDbPath(instance);
            GrimoraCliRunner.Run($"init --instance {instance}");
            BrainTestFixtures.InsertHardNode(dbPath, "rule:no-secrets-mcp", "rule", "No secrets in logs", "Never log a token.");

            Environment.SetEnvironmentVariable("GRIMORA_INSTANCE", instance);
            string expected = (string)McpDll.Invoke("brain_core")!;

            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = new BrainCoreTool().ExecuteMcp(connection);

            Assert.Equal(expected, actual);
            Assert.Contains("rule:no-secrets-mcp", actual);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GRIMORA_INSTANCE", previousInstanceEnv);
            GrimoraCliRunner.DeleteInstance(instance);
        }
    }

    [Fact]
    public void CliShapeReportsNothingWhenNoHardNodesExist()
    {
        string instance = GrimoraCliRunner.NewTestInstance("brain-core-cli-empty");
        try
        {
            GrimoraCliRunner.Run($"init --instance {instance}");

            (string stdout, int exitCode) = GrimoraCliRunner.Run($"brain core --instance {instance}");
            Assert.Equal(0, exitCode);
            string expected = stdout.Trim();

            string dbPath = GrimoraCliRunner.InstanceDbPath(instance);
            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = new BrainCoreTool().ExecuteCli(connection).Trim();

            Assert.Equal(expected, actual);
            Assert.Equal("(nothing)", actual);
        }
        finally
        {
            GrimoraCliRunner.DeleteInstance(instance);
        }
    }
}
