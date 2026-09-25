using Aitm.Brain.Tools;
using Aitm.Store.Data;
using Aitm.TestSupport;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Aitm.Brain.Tests;

public class BrainStatsToolTests
{
    [Fact]
    public void CliShapeMatchesTodaysCliOutputWithSeededNodes()
    {
        string oldInstance = AitmCliRunner.NewTestInstance("brain-stats-cli-old");
        string newInstance = AitmCliRunner.NewTestInstance("brain-stats-cli-new");
        try
        {
            // Oracle: today's aitm.cs BrainStats() (aitm.cs:1580).
            string oldDb = AitmCliRunner.InstanceDbPath(oldInstance);
            AitmCliRunner.Run($"init --instance {oldInstance}");
            SeedNodes(oldDb);
            (string stdout, int exitCode) = AitmCliRunner.Run($"brain stats --instance {oldInstance}");
            Assert.Equal(0, exitCode);
            string expected = stdout.Trim();

            string newDb = AitmCliRunner.InstanceDbPath(newInstance);
            AitmCliRunner.Run($"init --instance {newInstance}");
            SeedNodes(newDb);

            using SqliteConnection connection = StoreConnection.Open(newDb);
            string actual = new BrainStatsTool().Execute(connection).Trim();

            Assert.Equal(expected, actual);
            Assert.Contains("nodes", actual);
        }
        finally
        {
            AitmCliRunner.DeleteInstance(oldInstance);
            AitmCliRunner.DeleteInstance(newInstance);
        }
    }

    private static void SeedNodes(string dbPath)
    {
        BrainTestFixtures.InsertHardNode(dbPath, "rule:no-secrets", "rule", "No secrets in logs", "Never log a token.");
        BrainTestFixtures.InsertNode(dbPath, "concept:widget", "concept", "Widget", "a widget");
    }
}
