using Grimoira.Brain.Data;
using Grimoira.Facts.Tools;
using Grimoira.Store.Data;
using Grimoira.TestSupport;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Grimoira.Brain.Tests;

// RESTRUCTURE.md slice 16: "Brain now implements IUsageSignal, so channel lookups bump linked nodes as
// before. A test proves a fact hit still raises the node's usage.hits."
public class BrainUsageSignalTests
{
    [Fact]
    public void McpFactHitRaisesTheLinkedNodesUsageHits()
    {
        string instance = GrimoiraCliRunner.NewTestInstance("brain-usage-signal-fact-hit");
        try
        {
            string dbPath = GrimoiraCliRunner.InstanceDbPath(instance);
            GrimoiraCliRunner.Run($"init --instance {instance}");
            GrimoiraCliRunner.Run($"add --instance {instance} --term widgetfrobnicator --value the-answer --category manual");

            using SqliteConnection setup = StoreConnection.Open(dbPath);
            string factKey = ScalarText(setup, "SELECT k FROM facts WHERE term='widgetfrobnicator'");
            BrainTestFixtures.InsertNode(dbPath, "node:widget", "concept", "Widget", "the widget concept");
            BrainTestFixtures.InsertRef(dbPath, "node:widget", "facts", factKey);

            using SqliteConnection connection = StoreConnection.Open(dbPath);
            Assert.Equal(0L, ScalarLong(connection, "SELECT count(*) FROM usage WHERE node_k='node:widget'"));

            string result = new QueryTool(new BrainUsageSignal()).ExecuteMcp(connection, "widgetfrobnicator");

            Assert.Contains("the-answer", result);
            Assert.Equal(1L, ScalarLong(connection, "SELECT hits FROM usage WHERE node_k='node:widget'"));
        }
        finally
        {
            GrimoiraCliRunner.DeleteInstance(instance);
        }
    }

    // RESTRUCTURE.md slice 24 bullet 1: ports the `selftest` checks that exercised BrainUsage.Reinforce
    // directly (grimoira.cs's old SelfTest: "learn: reinforce raises hits + sets recency for a live node" /
    // "learn: reinforce ignores non-node keys (no junk usage rows)") now that selftest itself is gone.
    [Fact]
    public void ReinforceRaisesHitsAndSetsRecencyForALiveNode()
    {
        string instance = GrimoiraCliRunner.NewTestInstance("brain-usage-reinforce-live-node");
        try
        {
            GrimoiraCliRunner.Run($"init --instance {instance}");
            string dbPath = GrimoiraCliRunner.InstanceDbPath(instance);
            BrainTestFixtures.InsertNode(dbPath, "node:rank-a", "concept", "zorptron widget", "zorptron widget gizmo");

            using SqliteConnection connection = StoreConnection.Open(dbPath);
            Grimoira.Brain.Data.BrainUsage.Reinforce(connection, new[] { "node:rank-a" });
            Grimoira.Brain.Data.BrainUsage.Reinforce(connection, new[] { "node:rank-a", "not-a-node" });

            Assert.Equal(2L, ScalarLong(connection, "SELECT hits FROM usage WHERE node_k='node:rank-a'"));
            Assert.Equal(1L, ScalarLong(connection, "SELECT count(*) FROM usage WHERE node_k='node:rank-a' AND last_used IS NOT NULL"));
        }
        finally
        {
            GrimoiraCliRunner.DeleteInstance(instance);
        }
    }

    [Fact]
    public void ReinforceIgnoresNonNodeKeys()
    {
        string instance = GrimoiraCliRunner.NewTestInstance("brain-usage-reinforce-non-node");
        try
        {
            GrimoiraCliRunner.Run($"init --instance {instance}");
            string dbPath = GrimoiraCliRunner.InstanceDbPath(instance);
            using SqliteConnection connection = StoreConnection.Open(dbPath);

            Grimoira.Brain.Data.BrainUsage.Reinforce(connection, new[] { "not-a-node" });

            Assert.Equal(0L, ScalarLong(connection, "SELECT count(*) FROM usage WHERE node_k='not-a-node'"));
        }
        finally
        {
            GrimoiraCliRunner.DeleteInstance(instance);
        }
    }

    // Ports "learn: usage-blended recall ranks the reinforced node above an equal-relevance one": two
    // FTS-equal nodes, reinforce one, `brain recall` must surface the reinforced one first.
    [Fact]
    public void UsageBlendedRecallRanksTheReinforcedNodeAboveAnEqualRelevanceOne()
    {
        string instance = GrimoiraCliRunner.NewTestInstance("brain-usage-blended-ranking");
        try
        {
            GrimoiraCliRunner.Run($"init --instance {instance}");
            string dbPath = GrimoiraCliRunner.InstanceDbPath(instance);
            BrainTestFixtures.InsertNode(dbPath, "node:rank-a", "concept", "zorptron widget", "zorptron widget gizmo");
            BrainTestFixtures.InsertNode(dbPath, "node:rank-b", "concept", "zorptron widget", "zorptron widget gizmo");

            using SqliteConnection connection = StoreConnection.Open(dbPath);
            Grimoira.Brain.Data.BrainUsage.Reinforce(connection, new[] { "node:rank-a" });

            string result = new Grimoira.Brain.Tools.BrainRecallTool().ExecuteCli(connection, "zorptron");

            int indexA = result.IndexOf("node:rank-a", StringComparison.Ordinal);
            int indexB = result.IndexOf("node:rank-b", StringComparison.Ordinal);
            Assert.True(indexA >= 0 && indexB >= 0, "expected both nodes in the result");
            Assert.True(indexA < indexB, "reinforced node:rank-a should rank above node:rank-b");
        }
        finally
        {
            GrimoiraCliRunner.DeleteInstance(instance);
        }
    }

    private static long ScalarLong(SqliteConnection connection, string sql)
    {
        using SqliteCommand c = connection.CreateCommand();
        c.CommandText = sql;
        return (long)(c.ExecuteScalar() ?? 0L);
    }

    private static string ScalarText(SqliteConnection connection, string sql)
    {
        using SqliteCommand c = connection.CreateCommand();
        c.CommandText = sql;
        return (string)c.ExecuteScalar()!;
    }
}
