using Grimoira.Brain.Tools;
using Grimoira.Store.Data;
using Grimoira.TestSupport;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Grimoira.Brain.Tests;

public class BrainCoreToolTests
{
    [Fact]
    public void CliShapeMatchesTodaysCliOutputForHardNodes()
    {
        string oldInstance = GrimoiraCliRunner.NewTestInstance("brain-core-cli-old");
        string newInstance = GrimoiraCliRunner.NewTestInstance("brain-core-cli-new");
        try
        {
            // Oracle: today's grimoira.cs BrainCore() (grimoira.cs:1423).
            string oldDb = GrimoiraCliRunner.InstanceDbPath(oldInstance);
            GrimoiraCliRunner.Run($"init --instance {oldInstance}");
            BrainTestFixtures.InsertHardNode(oldDb, "rule:no-secrets", "rule", "No secrets in logs", "Never log a token.");
            (string stdout, int exitCode) = GrimoiraCliRunner.Run($"brain core --instance {oldInstance}");
            Assert.Equal(0, exitCode);
            string expected = stdout.Trim();

            string newDb = GrimoiraCliRunner.InstanceDbPath(newInstance);
            GrimoiraCliRunner.Run($"init --instance {newInstance}");
            BrainTestFixtures.InsertHardNode(newDb, "rule:no-secrets", "rule", "No secrets in logs", "Never log a token.");

            using SqliteConnection connection = StoreConnection.Open(newDb);
            string actual = new BrainCoreTool().ExecuteCli(connection).Trim();

            Assert.Equal(expected, actual);
            Assert.Contains("rule:no-secrets", actual);
        }
        finally
        {
            GrimoiraCliRunner.DeleteInstance(oldInstance);
            GrimoiraCliRunner.DeleteInstance(newInstance);
        }
    }

    [Fact]
    public void McpShapeMatchesTodaysMcpOutputForHardNodes()
    {
        string instance = GrimoiraCliRunner.NewTestInstance("brain-core-mcp");
        string? previousInstanceEnv = Environment.GetEnvironmentVariable("GRIMOIRA_INSTANCE");
        try
        {
            string dbPath = GrimoiraCliRunner.InstanceDbPath(instance);
            GrimoiraCliRunner.Run($"init --instance {instance}");
            BrainTestFixtures.InsertHardNode(dbPath, "rule:no-secrets-mcp", "rule", "No secrets in logs", "Never log a token.");

            Environment.SetEnvironmentVariable("GRIMOIRA_INSTANCE", instance);
            string expected = (string)McpDll.Invoke("brain_core")!;

            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = new BrainCoreTool().ExecuteMcp(connection);

            Assert.Equal(expected, actual);
            Assert.Contains("rule:no-secrets-mcp", actual);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GRIMOIRA_INSTANCE", previousInstanceEnv);
            GrimoiraCliRunner.DeleteInstance(instance);
        }
    }

    [Fact]
    public void CliShapeReportsNothingWhenNoHardNodesExist()
    {
        string instance = GrimoiraCliRunner.NewTestInstance("brain-core-cli-empty");
        try
        {
            GrimoiraCliRunner.Run($"init --instance {instance}");

            (string stdout, int exitCode) = GrimoiraCliRunner.Run($"brain core --instance {instance}");
            Assert.Equal(0, exitCode);
            string expected = stdout.Trim();

            string dbPath = GrimoiraCliRunner.InstanceDbPath(instance);
            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = new BrainCoreTool().ExecuteCli(connection).Trim();

            Assert.Equal(expected, actual);
            Assert.Equal("(nothing)", actual);
        }
        finally
        {
            GrimoiraCliRunner.DeleteInstance(instance);
        }
    }
}
