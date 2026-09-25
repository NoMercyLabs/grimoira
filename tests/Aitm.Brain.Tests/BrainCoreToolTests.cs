using Aitm.Brain.Tools;
using Aitm.Store.Data;
using Aitm.TestSupport;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Aitm.Brain.Tests;

public class BrainCoreToolTests
{
    [Fact]
    public void CliShapeMatchesTodaysCliOutputForHardNodes()
    {
        string oldInstance = AitmCliRunner.NewTestInstance("brain-core-cli-old");
        string newInstance = AitmCliRunner.NewTestInstance("brain-core-cli-new");
        try
        {
            // Oracle: today's aitm.cs BrainCore() (aitm.cs:1423).
            string oldDb = AitmCliRunner.InstanceDbPath(oldInstance);
            AitmCliRunner.Run($"init --instance {oldInstance}");
            BrainTestFixtures.InsertHardNode(oldDb, "rule:no-secrets", "rule", "No secrets in logs", "Never log a token.");
            (string stdout, int exitCode) = AitmCliRunner.Run($"brain core --instance {oldInstance}");
            Assert.Equal(0, exitCode);
            string expected = stdout.Trim();

            string newDb = AitmCliRunner.InstanceDbPath(newInstance);
            AitmCliRunner.Run($"init --instance {newInstance}");
            BrainTestFixtures.InsertHardNode(newDb, "rule:no-secrets", "rule", "No secrets in logs", "Never log a token.");

            using SqliteConnection connection = StoreConnection.Open(newDb);
            string actual = new BrainCoreTool().ExecuteCli(connection).Trim();

            Assert.Equal(expected, actual);
            Assert.Contains("rule:no-secrets", actual);
        }
        finally
        {
            AitmCliRunner.DeleteInstance(oldInstance);
            AitmCliRunner.DeleteInstance(newInstance);
        }
    }

    [Fact]
    public void McpShapeMatchesTodaysMcpOutputForHardNodes()
    {
        string instance = AitmCliRunner.NewTestInstance("brain-core-mcp");
        string? previousInstanceEnv = Environment.GetEnvironmentVariable("AITM_INSTANCE");
        try
        {
            string dbPath = AitmCliRunner.InstanceDbPath(instance);
            AitmCliRunner.Run($"init --instance {instance}");
            BrainTestFixtures.InsertHardNode(dbPath, "rule:no-secrets-mcp", "rule", "No secrets in logs", "Never log a token.");

            Environment.SetEnvironmentVariable("AITM_INSTANCE", instance);
            string expected = (string)McpDll.Invoke("brain_core")!;

            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = new BrainCoreTool().ExecuteMcp(connection);

            Assert.Equal(expected, actual);
            Assert.Contains("rule:no-secrets-mcp", actual);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AITM_INSTANCE", previousInstanceEnv);
            AitmCliRunner.DeleteInstance(instance);
        }
    }

    [Fact]
    public void CliShapeReportsNothingWhenNoHardNodesExist()
    {
        string instance = AitmCliRunner.NewTestInstance("brain-core-cli-empty");
        try
        {
            AitmCliRunner.Run($"init --instance {instance}");

            (string stdout, int exitCode) = AitmCliRunner.Run($"brain core --instance {instance}");
            Assert.Equal(0, exitCode);
            string expected = stdout.Trim();

            string dbPath = AitmCliRunner.InstanceDbPath(instance);
            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = new BrainCoreTool().ExecuteCli(connection).Trim();

            Assert.Equal(expected, actual);
            Assert.Equal("(nothing)", actual);
        }
        finally
        {
            AitmCliRunner.DeleteInstance(instance);
        }
    }
}
