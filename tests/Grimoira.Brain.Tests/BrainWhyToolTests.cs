using Grimoira.Brain.Tools;
using Grimoira.Store.Data;
using Grimoira.TestSupport;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Grimoira.Brain.Tests;

public class BrainWhyToolTests
{
    [Fact]
    public void CliShapeMatchesTodaysCliOutputForALiveNode()
    {
        string oldInstance = GrimoiraCliRunner.NewTestInstance("brain-why-cli-old");
        string newInstance = GrimoiraCliRunner.NewTestInstance("brain-why-cli-new");
        try
        {
            // Oracle: today's grimoira.cs BrainWhy() (grimoira.cs:1838).
            string oldDb = GrimoiraCliRunner.InstanceDbPath(oldInstance);
            GrimoiraCliRunner.Run($"init --instance {oldInstance}");
            SeedNode(oldDb);
            (string stdout, int exitCode) = GrimoiraCliRunner.Run($"brain why --instance {oldInstance} proj:web");
            Assert.Equal(0, exitCode);
            string expected = stdout.Trim();

            string newDb = GrimoiraCliRunner.InstanceDbPath(newInstance);
            GrimoiraCliRunner.Run($"init --instance {newInstance}");
            SeedNode(newDb);

            using SqliteConnection connection = StoreConnection.Open(newDb);
            string actual = new BrainWhyTool().Execute(connection, "proj:web").Trim();

            Assert.Equal(expected, actual);
            Assert.Contains("seam:device_id", actual);
        }
        finally
        {
            GrimoiraCliRunner.DeleteInstance(oldInstance);
            GrimoiraCliRunner.DeleteInstance(newInstance);
        }
    }

    [Fact]
    public void CliShapeReportsNoLiveNodeForAnUnknownKey()
    {
        string instance = GrimoiraCliRunner.NewTestInstance("brain-why-cli-unknown");
        try
        {
            GrimoiraCliRunner.Run($"init --instance {instance}");

            (string stdout, int exitCode) = GrimoiraCliRunner.Run($"brain why --instance {instance} no-such-node");
            Assert.Equal(0, exitCode);
            string expected = stdout.Trim();

            string dbPath = GrimoiraCliRunner.InstanceDbPath(instance);
            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = new BrainWhyTool().Execute(connection, "no-such-node").Trim();

            Assert.Equal(expected, actual);
            Assert.Equal("no live node 'no-such-node'.", actual);
        }
        finally
        {
            GrimoiraCliRunner.DeleteInstance(instance);
        }
    }

    private static void SeedNode(string dbPath)
    {
        BrainTestFixtures.InsertNode(dbPath, "proj:web", "proj", "Web", "Web app");
        BrainTestFixtures.InsertNode(dbPath, "seam:device_id", "seam", "device_id", "shared device identifier field");
        BrainTestFixtures.InsertSharingTriple(dbPath, "proj:web", "seam:device_id");
    }
}
