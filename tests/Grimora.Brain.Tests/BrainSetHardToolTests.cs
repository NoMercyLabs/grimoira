using Grimora.Brain.Tools;
using Grimora.Store.Data;
using Grimora.TestSupport;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Grimora.Brain.Tests;

public class BrainSetHardToolTests
{
    [Fact]
    public void CliShapeMatchesTodaysCliOutputAndFlipsTheFlagThroughSupersession()
    {
        string oldInstance = GrimoraCliRunner.NewTestInstance("brain-set-hard-old");
        string newInstance = GrimoraCliRunner.NewTestInstance("brain-set-hard-new");
        try
        {
            // Oracle: today's grimora.cs BrainSetHard() (grimora.cs:1730).
            string oldDb = GrimoraCliRunner.InstanceDbPath(oldInstance);
            GrimoraCliRunner.Run($"init --instance {oldInstance}");
            BrainTestFixtures.InsertNode(oldDb, "sethard:n1", "concept", "a widget", "widget gloss");
            (string stdout, int exitCode) = GrimoraCliRunner.Run($"brain set-hard --instance {oldInstance} sethard:n1 1");
            Assert.Equal(0, exitCode);
            string expected = stdout.Trim();

            string newDb = GrimoraCliRunner.InstanceDbPath(newInstance);
            GrimoraCliRunner.Run($"init --instance {newInstance}");
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
            GrimoraCliRunner.DeleteInstance(oldInstance);
            GrimoraCliRunner.DeleteInstance(newInstance);
        }
    }

    [Fact]
    public void CliShapeReportsAMissingNode()
    {
        string instance = GrimoraCliRunner.NewTestInstance("brain-set-hard-missing");
        try
        {
            GrimoraCliRunner.Run($"init --instance {instance}");
            (string stdout, int exitCode) = GrimoraCliRunner.Run($"brain set-hard --instance {instance} sethard:missing 1");
            Assert.Equal(0, exitCode);
            string expected = stdout.Trim();

            string dbPath = GrimoraCliRunner.InstanceDbPath(instance);
            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = new BrainSetHardTool().Execute(connection, "sethard:missing", true).Trim();

            Assert.Equal(expected, actual);
            Assert.Equal("no live node 'sethard:missing'.", actual);
        }
        finally
        {
            GrimoraCliRunner.DeleteInstance(instance);
        }
    }
}
