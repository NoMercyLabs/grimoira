using Grimoira.Brain.Tools;
using Grimoira.Store.Data;
using Grimoira.TestSupport;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Grimoira.Brain.Tests;

public class BrainSetHardToolTests
{
    [Fact]
    public void CliShapeMatchesTodaysCliOutputAndFlipsTheFlagThroughSupersession()
    {
        string oldInstance = GrimoiraCliRunner.NewTestInstance("brain-set-hard-old");
        string newInstance = GrimoiraCliRunner.NewTestInstance("brain-set-hard-new");
        try
        {
            // Oracle: today's grimoira.cs BrainSetHard() (grimoira.cs:1730).
            string oldDb = GrimoiraCliRunner.InstanceDbPath(oldInstance);
            GrimoiraCliRunner.Run($"init --instance {oldInstance}");
            BrainTestFixtures.InsertNode(oldDb, "sethard:n1", "concept", "a widget", "widget gloss");
            (string stdout, int exitCode) = GrimoiraCliRunner.Run($"brain set-hard --instance {oldInstance} sethard:n1 1");
            Assert.Equal(0, exitCode);
            string expected = stdout.Trim();

            string newDb = GrimoiraCliRunner.InstanceDbPath(newInstance);
            GrimoiraCliRunner.Run($"init --instance {newInstance}");
            BrainTestFixtures.InsertNode(newDb, "sethard:n1", "concept", "a widget", "widget gloss");
            using SqliteConnection connection = StoreConnection.Open(newDb);
            string actual = new BrainSetHardTool().Execute(connection, "sethard:n1", true).Trim();

            Assert.Equal(expected, actual);
            Assert.Equal("sethard:n1 hard=1.", actual);

            using SqliteCommand hardNow = connection.CreateCommand();
            hardNow.CommandText = "SELECT hard FROM node_now WHERE k='sethard:n1'";
            Assert.Equal(1L, (long)hardNow.ExecuteScalar()!);

            // Supersession, not a raw UPDATE: exactly one live row, but two rows in the node table's history.
            using SqliteCommand liveCount = connection.CreateCommand();
            liveCount.CommandText = "SELECT count(*) FROM node_now WHERE k='sethard:n1'";
            Assert.Equal(1L, (long)liveCount.ExecuteScalar()!);

            using SqliteCommand historyCount = connection.CreateCommand();
            historyCount.CommandText = "SELECT count(*) FROM node WHERE k='sethard:n1'";
            Assert.Equal(2L, (long)historyCount.ExecuteScalar()!);

            using SqliteCommand mutation = connection.CreateCommand();
            mutation.CommandText = "SELECT count(*) FROM mutations WHERE kind='node' AND k='sethard:n1' AND why='set-hard'";
            Assert.Equal(1L, (long)mutation.ExecuteScalar()!);
        }
        finally
        {
            GrimoiraCliRunner.DeleteInstance(oldInstance);
            GrimoiraCliRunner.DeleteInstance(newInstance);
        }
    }

    [Fact]
    public void CliShapeReportsAMissingNode()
    {
        string instance = GrimoiraCliRunner.NewTestInstance("brain-set-hard-missing");
        try
        {
            GrimoiraCliRunner.Run($"init --instance {instance}");
            (string stdout, int exitCode) = GrimoiraCliRunner.Run($"brain set-hard --instance {instance} sethard:missing 1");
            Assert.Equal(0, exitCode);
            string expected = stdout.Trim();

            string dbPath = GrimoiraCliRunner.InstanceDbPath(instance);
            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = new BrainSetHardTool().Execute(connection, "sethard:missing", true).Trim();

            Assert.Equal(expected, actual);
            Assert.Equal("no live node 'sethard:missing'.", actual);
        }
        finally
        {
            GrimoiraCliRunner.DeleteInstance(instance);
        }
    }
}
