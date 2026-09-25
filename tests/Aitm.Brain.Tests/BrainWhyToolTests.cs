using Aitm.Brain.Tools;
using Aitm.Store.Data;
using Aitm.TestSupport;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Aitm.Brain.Tests;

public class BrainWhyToolTests
{
    [Fact]
    public void CliShapeMatchesTodaysCliOutputForALiveNode()
    {
        string oldInstance = AitmCliRunner.NewTestInstance("brain-why-cli-old");
        string newInstance = AitmCliRunner.NewTestInstance("brain-why-cli-new");
        try
        {
            // Oracle: today's aitm.cs BrainWhy() (aitm.cs:1838).
            string oldDb = AitmCliRunner.InstanceDbPath(oldInstance);
            AitmCliRunner.Run($"init --instance {oldInstance}");
            SeedNode(oldDb);
            (string stdout, int exitCode) = AitmCliRunner.Run($"brain why --instance {oldInstance} proj:web");
            Assert.Equal(0, exitCode);
            string expected = stdout.Trim();

            string newDb = AitmCliRunner.InstanceDbPath(newInstance);
            AitmCliRunner.Run($"init --instance {newInstance}");
            SeedNode(newDb);

            using SqliteConnection connection = StoreConnection.Open(newDb);
            string actual = new BrainWhyTool().Execute(connection, "proj:web").Trim();

            Assert.Equal(expected, actual);
            Assert.Contains("seam:device_id", actual);
        }
        finally
        {
            AitmCliRunner.DeleteInstance(oldInstance);
            AitmCliRunner.DeleteInstance(newInstance);
        }
    }

    [Fact]
    public void CliShapeReportsNoLiveNodeForAnUnknownKey()
    {
        string instance = AitmCliRunner.NewTestInstance("brain-why-cli-unknown");
        try
        {
            AitmCliRunner.Run($"init --instance {instance}");

            (string stdout, int exitCode) = AitmCliRunner.Run($"brain why --instance {instance} no-such-node");
            Assert.Equal(0, exitCode);
            string expected = stdout.Trim();

            string dbPath = AitmCliRunner.InstanceDbPath(instance);
            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = new BrainWhyTool().Execute(connection, "no-such-node").Trim();

            Assert.Equal(expected, actual);
            Assert.Equal("no live node 'no-such-node'.", actual);
        }
        finally
        {
            AitmCliRunner.DeleteInstance(instance);
        }
    }

    private static void SeedNode(string dbPath)
    {
        BrainTestFixtures.InsertNode(dbPath, "proj:web", "proj", "Web", "Web app");
        BrainTestFixtures.InsertNode(dbPath, "seam:device_id", "seam", "device_id", "shared device identifier field");
        BrainTestFixtures.InsertSharingTriple(dbPath, "proj:web", "seam:device_id");
    }
}
