using Grimoira.Brain.Tools;
using Grimoira.Store.Data;
using Grimoira.TestSupport;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Grimoira.Brain.Tests;

public class BrainStatsToolTests
{
    [Fact]
    public void CliShapeMatchesTodaysCliOutputWithSeededNodes()
    {
        string oldInstance = GrimoiraCliRunner.NewTestInstance("brain-stats-cli-old");
        string newInstance = GrimoiraCliRunner.NewTestInstance("brain-stats-cli-new");
        try
        {
            // Oracle: today's grimoira.cs BrainStats() (grimoira.cs:1580).
            string oldDb = GrimoiraCliRunner.InstanceDbPath(oldInstance);
            GrimoiraCliRunner.Run($"init --instance {oldInstance}");
            SeedNodes(oldDb);
            (string stdout, int exitCode) = GrimoiraCliRunner.Run($"brain stats --instance {oldInstance}");
            Assert.Equal(0, exitCode);
            string expected = stdout.Trim();

            string newDb = GrimoiraCliRunner.InstanceDbPath(newInstance);
            GrimoiraCliRunner.Run($"init --instance {newInstance}");
            SeedNodes(newDb);

            using SqliteConnection connection = StoreConnection.Open(newDb);
            string actual = new BrainStatsTool().Execute(connection).Trim();

            Assert.Equal(expected, actual);
            Assert.Contains("nodes", actual);
        }
        finally
        {
            GrimoiraCliRunner.DeleteInstance(oldInstance);
            GrimoiraCliRunner.DeleteInstance(newInstance);
        }
    }

    private static void SeedNodes(string dbPath)
    {
        BrainTestFixtures.InsertHardNode(dbPath, "rule:no-secrets", "rule", "No secrets in logs", "Never log a token.");
        BrainTestFixtures.InsertNode(dbPath, "concept:widget", "concept", "Widget", "a widget");
    }
}
