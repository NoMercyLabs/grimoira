using Grimora.Brain.Tools;
using Grimora.Store.Data;
using Grimora.TestSupport;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Grimora.Brain.Tests;

public class BrainWhyToolTests
{
    [Fact]
    public void CliShapeMatchesTodaysCliOutputForALiveNode()
    {
        string oldInstance = GrimoraCliRunner.NewTestInstance("brain-why-cli-old");
        string newInstance = GrimoraCliRunner.NewTestInstance("brain-why-cli-new");
        try
        {
            // Oracle: today's grimora.cs BrainWhy() (grimora.cs:1838).
            string oldDb = GrimoraCliRunner.InstanceDbPath(oldInstance);
            GrimoraCliRunner.Run($"init --instance {oldInstance}");
            SeedNode(oldDb);
            (string stdout, int exitCode) = GrimoraCliRunner.Run($"brain why --instance {oldInstance} proj:web");
            Assert.Equal(0, exitCode);
            string expected = stdout.Trim();

            string newDb = GrimoraCliRunner.InstanceDbPath(newInstance);
            GrimoraCliRunner.Run($"init --instance {newInstance}");
            SeedNode(newDb);

            using SqliteConnection connection = StoreConnection.Open(newDb);
            string actual = new BrainWhyTool().Execute(connection, "proj:web").Trim();

            Assert.Equal(expected, actual);
            Assert.Contains("seam:device_id", actual);
        }
        finally
        {
            GrimoraCliRunner.DeleteInstance(oldInstance);
            GrimoraCliRunner.DeleteInstance(newInstance);
        }
    }

    [Fact]
    public void CliShapeReportsNoLiveNodeForAnUnknownKey()
    {
        string instance = GrimoraCliRunner.NewTestInstance("brain-why-cli-unknown");
        try
        {
            GrimoraCliRunner.Run($"init --instance {instance}");

            (string stdout, int exitCode) = GrimoraCliRunner.Run($"brain why --instance {instance} no-such-node");
            Assert.Equal(0, exitCode);
            string expected = stdout.Trim();

            string dbPath = GrimoraCliRunner.InstanceDbPath(instance);
            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = new BrainWhyTool().Execute(connection, "no-such-node").Trim();

            Assert.Equal(expected, actual);
            Assert.Equal("no live node 'no-such-node'.", actual);
        }
        finally
        {
            GrimoraCliRunner.DeleteInstance(instance);
        }
    }

    private static void SeedNode(string dbPath)
    {
        BrainTestFixtures.InsertNode(dbPath, "proj:web", "proj", "Web", "Web app");
        BrainTestFixtures.InsertNode(dbPath, "seam:device_id", "seam", "device_id", "shared device identifier field");
        BrainTestFixtures.InsertSharingTriple(dbPath, "proj:web", "seam:device_id");
    }
}
