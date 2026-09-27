using Grimora.Brain.Tools;
using Grimora.Store.Data;
using Grimora.TestSupport;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Grimora.Brain.Tests;

public class BrainStatsToolTests
{
    [Fact]
    public void CliShapeMatchesTodaysCliOutputWithSeededNodes()
    {
        string oldInstance = GrimoraCliRunner.NewTestInstance("brain-stats-cli-old");
        string newInstance = GrimoraCliRunner.NewTestInstance("brain-stats-cli-new");
        try
        {
            // Oracle: today's grimora.cs BrainStats() (grimora.cs:1580).
            string oldDb = GrimoraCliRunner.InstanceDbPath(oldInstance);
            GrimoraCliRunner.Run($"init --instance {oldInstance}");
            SeedNodes(oldDb);
            (string stdout, int exitCode) = GrimoraCliRunner.Run($"brain stats --instance {oldInstance}");
            Assert.Equal(0, exitCode);
            string expected = stdout.Trim();

            string newDb = GrimoraCliRunner.InstanceDbPath(newInstance);
            GrimoraCliRunner.Run($"init --instance {newInstance}");
            SeedNodes(newDb);

            using SqliteConnection connection = StoreConnection.Open(newDb);
            string actual = new BrainStatsTool().Execute(connection).Trim();

            Assert.Equal(expected, actual);
            Assert.Contains("nodes", actual);
        }
        finally
        {
            GrimoraCliRunner.DeleteInstance(oldInstance);
            GrimoraCliRunner.DeleteInstance(newInstance);
        }
    }

    private static void SeedNodes(string dbPath)
    {
        BrainTestFixtures.InsertHardNode(dbPath, "rule:no-secrets", "rule", "No secrets in logs", "Never log a token.");
        BrainTestFixtures.InsertNode(dbPath, "concept:widget", "concept", "Widget", "a widget");
    }
}
