using Grimoira.Brain.Tools;
using Grimoira.Store.Data;
using Grimoira.TestSupport;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Grimoira.Brain.Tests;

public class BrainStaleToolTests
{
    [Fact]
    public void CliShapeMatchesTodaysCliOutputWithNoStaleNodes()
    {
        string oldInstance = GrimoiraCliRunner.NewTestInstance("brain-stale-cli-old");
        string newInstance = GrimoiraCliRunner.NewTestInstance("brain-stale-cli-new");
        try
        {
            // Oracle: today's grimoira.cs BrainStale() (grimoira.cs:1783), default --days 30 (grimoira.cs:1350).
            string oldDb = GrimoiraCliRunner.InstanceDbPath(oldInstance);
            GrimoiraCliRunner.Run($"init --instance {oldInstance}");
            BrainTestFixtures.InsertNode(oldDb, "concept:fresh", "concept", "Fresh", "just learned");
            (string stdout, int exitCode) = GrimoiraCliRunner.Run($"brain stale --instance {oldInstance}");
            Assert.Equal(0, exitCode);
            string expected = stdout.Trim();

            string newDb = GrimoiraCliRunner.InstanceDbPath(newInstance);
            GrimoiraCliRunner.Run($"init --instance {newInstance}");
            BrainTestFixtures.InsertNode(newDb, "concept:fresh", "concept", "Fresh", "just learned");

            using SqliteConnection connection = StoreConnection.Open(newDb);
            string actual = new BrainStaleTool().Execute(connection).Trim();

            Assert.Equal(expected, actual);
            Assert.Contains("0 node(s) older than 30d", actual);
        }
        finally
        {
            GrimoiraCliRunner.DeleteInstance(oldInstance);
            GrimoiraCliRunner.DeleteInstance(newInstance);
        }
    }

    [Fact]
    public void CliShapeMatchesTodaysCliOutputWithAOneDayWindow()
    {
        string oldInstance = GrimoiraCliRunner.NewTestInstance("brain-stale-cli-old-1d");
        string newInstance = GrimoiraCliRunner.NewTestInstance("brain-stale-cli-new-1d");
        try
        {
            // A node backdated 3 days and never verified is stale for a 1-day window, whatever the clock
            // granularity — proves the tool surfaces a real row, not just the empty-report shape.
            string oldDb = GrimoiraCliRunner.InstanceDbPath(oldInstance);
            GrimoiraCliRunner.Run($"init --instance {oldInstance}");
            BrainTestFixtures.InsertNodeAged(oldDb, "concept:old", "concept", "Old", "asserted a while ago", 3);
            (string stdout, int exitCode) = GrimoiraCliRunner.Run($"brain stale --instance {oldInstance} --days 1");
            Assert.Equal(0, exitCode);
            string expected = stdout.Trim();

            string newDb = GrimoiraCliRunner.InstanceDbPath(newInstance);
            GrimoiraCliRunner.Run($"init --instance {newInstance}");
            BrainTestFixtures.InsertNodeAged(newDb, "concept:old", "concept", "Old", "asserted a while ago", 3);

            using SqliteConnection connection = StoreConnection.Open(newDb);
            string actual = new BrainStaleTool().Execute(connection, 1).Trim();

            Assert.Equal(expected, actual);
            Assert.Contains("concept:old", actual);
            Assert.Contains("never confirmed", actual);
        }
        finally
        {
            GrimoiraCliRunner.DeleteInstance(oldInstance);
            GrimoiraCliRunner.DeleteInstance(newInstance);
        }
    }
}
