using Aitm.Brain.Data;
using Aitm.Facts.Tools;
using Aitm.Store.Data;
using Aitm.TestSupport;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Aitm.Brain.Tests;

// RESTRUCTURE.md slice 16: "Brain now implements IUsageSignal, so channel lookups bump linked nodes as
// before. A test proves a fact hit still raises the node's usage.hits."
public class BrainUsageSignalTests
{
    [Fact]
    public void McpFactHitRaisesTheLinkedNodesUsageHits()
    {
        string instance = AitmCliRunner.NewTestInstance("brain-usage-signal-fact-hit");
        try
        {
            string dbPath = AitmCliRunner.InstanceDbPath(instance);
            AitmCliRunner.Run($"init --instance {instance}");
            AitmCliRunner.Run($"add --instance {instance} --term widgetfrobnicator --value the-answer --category manual");

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
            AitmCliRunner.DeleteInstance(instance);
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
