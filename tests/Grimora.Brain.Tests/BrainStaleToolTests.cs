using Grimora.Brain.Tools;
using Grimora.Store.Data;
using Grimora.TestSupport;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Grimora.Brain.Tests;

public class BrainStaleToolTests
{
    [Fact]
    public void CliShapeMatchesTodaysCliOutputWithNoStaleNodes()
    {
        string oldInstance = GrimoraCliRunner.NewTestInstance("brain-stale-cli-old");
        string newInstance = GrimoraCliRunner.NewTestInstance("brain-stale-cli-new");
        try
        {
            // Oracle: today's grimora.cs BrainStale() (grimora.cs:1783), default --days 30 (grimora.cs:1350).
            string oldDb = GrimoraCliRunner.InstanceDbPath(oldInstance);
            GrimoraCliRunner.Run($"init --instance {oldInstance}");
            BrainTestFixtures.InsertNode(oldDb, "concept:fresh", "concept", "Fresh", "just learned");
            (string stdout, int exitCode) = GrimoraCliRunner.Run($"brain stale --instance {oldInstance}");
            Assert.Equal(0, exitCode);
            string expected = stdout.Trim();

            string newDb = GrimoraCliRunner.InstanceDbPath(newInstance);
            GrimoraCliRunner.Run($"init --instance {newInstance}");
            BrainTestFixtures.InsertNode(newDb, "concept:fresh", "concept", "Fresh", "just learned");

            using SqliteConnection connection = StoreConnection.Open(newDb);
            string actual = new BrainStaleTool().Execute(connection).Trim();

            Assert.Equal(expected, actual);
            Assert.Contains("0 node(s) older than 30d", actual);
        }
        finally
        {
            GrimoraCliRunner.DeleteInstance(oldInstance);
            GrimoraCliRunner.DeleteInstance(newInstance);
        }
    }

    [Fact]
    public void CliShapeMatchesTodaysCliOutputWithAZeroDayWindow()
    {
        string oldInstance = GrimoraCliRunner.NewTestInstance("brain-stale-cli-old-0d");
        string newInstance = GrimoraCliRunner.NewTestInstance("brain-stale-cli-new-0d");
        try
        {
            // A 0-day window makes every just-inserted, never-verified node "stale" immediately —
            // proves the tool surfaces a real row, not just the empty-report shape.
            string oldDb = GrimoraCliRunner.InstanceDbPath(oldInstance);
            GrimoraCliRunner.Run($"init --instance {oldInstance}");
            BrainTestFixtures.InsertNode(oldDb, "concept:old", "concept", "Old", "asserted a while ago");
            (string stdout, int exitCode) = GrimoraCliRunner.Run($"brain stale --instance {oldInstance} --days 0");
            Assert.Equal(0, exitCode);
            string expected = stdout.Trim();

            string newDb = GrimoraCliRunner.InstanceDbPath(newInstance);
            GrimoraCliRunner.Run($"init --instance {newInstance}");
            BrainTestFixtures.InsertNode(newDb, "concept:old", "concept", "Old", "asserted a while ago");

            using SqliteConnection connection = StoreConnection.Open(newDb);
            string actual = new BrainStaleTool().Execute(connection, 0).Trim();

            Assert.Equal(expected, actual);
            Assert.Contains("concept:old", actual);
            Assert.Contains("never confirmed", actual);
        }
        finally
        {
            GrimoraCliRunner.DeleteInstance(oldInstance);
            GrimoraCliRunner.DeleteInstance(newInstance);
        }
    }
}
