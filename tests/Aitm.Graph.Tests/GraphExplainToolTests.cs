using Aitm.Graph.Tools;
using Aitm.Store.Data;
using Aitm.TestSupport;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Aitm.Graph.Tests;

// Ports mcp-graph.test.mjs's graph_explain case (declaration site, users grouped by project) plus the
// CLI oracle pin from aitm.cs's own selftest fixture (aitm.cs:3290-3325).
public class GraphExplainToolTests
{
    [Fact]
    public void CliShapeMatchesTodaysCliOutputForARecordedSymbol()
    {
        string oldInstance = AitmCliRunner.NewTestInstance("graph-explain-cli-old");
        string newInstance = AitmCliRunner.NewTestInstance("graph-explain-cli-new");
        try
        {
            // Oracle: today's aitm.cs GraphExplainCmd() (aitm.cs:2733).
            string oldDb = AitmCliRunner.InstanceDbPath(oldInstance);
            AitmCliRunner.Run($"init --instance {oldInstance}");
            SeedFixture(oldDb);
            (string stdout, int exitCode) = AitmCliRunner.Run($"graph-explain --instance {oldInstance} GraphFixtureWidget");
            Assert.Equal(0, exitCode);
            string expected = Normalize(stdout);

            string newDb = AitmCliRunner.InstanceDbPath(newInstance);
            AitmCliRunner.Run($"init --instance {newInstance}");
            SeedFixture(newDb);

            using SqliteConnection connection = StoreConnection.Open(newDb);
            string actual = Normalize(new GraphExplainTool().ExecuteCli(connection, "GraphFixtureWidget"));

            Assert.Equal(expected, actual);
            Assert.Contains("ts declaration", actual);
            Assert.Contains("alpha/widget.ts:10", actual);
        }
        finally
        {
            AitmCliRunner.DeleteInstance(oldInstance);
            AitmCliRunner.DeleteInstance(newInstance);
        }
    }

    [Fact]
    public void CliShapeReportsNoSymbolMatchForAnUnknownSymbol()
    {
        string instance = AitmCliRunner.NewTestInstance("graph-explain-cli-unknown");
        try
        {
            AitmCliRunner.Run($"init --instance {instance}");

            (string stdout, int exitCode) = AitmCliRunner.Run($"graph-explain --instance {instance} never-indexed-symbol");
            Assert.Equal(0, exitCode);
            string expected = stdout.Trim();

            string dbPath = AitmCliRunner.InstanceDbPath(instance);
            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = new GraphExplainTool().ExecuteCli(connection, "never-indexed-symbol").Trim();

            Assert.Equal(expected, actual);
            Assert.Contains("no symbol matches", actual);
        }
        finally
        {
            AitmCliRunner.DeleteInstance(instance);
        }
    }

    // Ported from mcp-graph.test.mjs: 'graph_explain: shows the declaration site' / 'lists users grouped
    // by project'. mcp.cs's graph_explain resolves its own connection from AITM_INSTANCE.
    [Fact]
    public void McpShapeMatchesTodaysMcpOutputForARecordedSymbol()
    {
        string instance = AitmCliRunner.NewTestInstance("graph-explain-mcp-hit");
        string? previousInstanceEnv = Environment.GetEnvironmentVariable("AITM_INSTANCE");
        try
        {
            string dbPath = AitmCliRunner.InstanceDbPath(instance);
            AitmCliRunner.Run($"init --instance {instance}");
            SeedFixture(dbPath);

            Environment.SetEnvironmentVariable("AITM_INSTANCE", instance);
            string expected = (string)GraphQueryToolTests.InvokeMcp("graph_explain", "GraphFixtureWidget")!;

            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = new GraphExplainTool().ExecuteMcp(connection, "GraphFixtureWidget");

            Assert.Equal(expected, actual);
            Assert.Contains("alpha/widget.ts:10", actual);
            Assert.Contains("beta", actual);
            Assert.Contains("gamma", actual);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AITM_INSTANCE", previousInstanceEnv);
            AitmCliRunner.DeleteInstance(instance);
        }
    }

    [Fact]
    public void McpShapeReportsNoSymbolMatchForAnUnknownSymbol()
    {
        string instance = AitmCliRunner.NewTestInstance("graph-explain-mcp-unknown");
        string? previousInstanceEnv = Environment.GetEnvironmentVariable("AITM_INSTANCE");
        try
        {
            AitmCliRunner.Run($"init --instance {instance}");

            Environment.SetEnvironmentVariable("AITM_INSTANCE", instance);
            string expected = (string)GraphQueryToolTests.InvokeMcp("graph_explain", "never-indexed-mcp-symbol")!;

            string dbPath = AitmCliRunner.InstanceDbPath(instance);
            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = new GraphExplainTool().ExecuteMcp(connection, "never-indexed-mcp-symbol");

            Assert.Equal(expected, actual);
            Assert.Contains("no symbol matches", actual);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AITM_INSTANCE", previousInstanceEnv);
            AitmCliRunner.DeleteInstance(instance);
        }
    }

    private static void SeedFixture(string dbPath)
    {
        using SqliteConnection connection = StoreConnection.Open(dbPath);
        GraphQueryToolTests.InsertEdge(connection, "GraphFixtureWidget", "decl", "alpha", "alpha/widget.ts", 10, "ts declaration");
        GraphQueryToolTests.InsertEdge(connection, "GraphFixtureWidget", "", "beta", "beta/x.ts", 5, "uses widget");
        GraphQueryToolTests.InsertEdge(connection, "GraphFixtureWidget", "", "beta", "beta/z.ts", 1, "uses widget again");
        GraphQueryToolTests.InsertEdge(connection, "GraphFixtureWidget", "", "gamma", "gamma/y.ts", 9, "uses widget too");
    }

    // Windows redirects the child's stdout through the OEM codepage, not UTF-8, so aitm.cs's "—" arrives
    // mangled regardless of the encoding this side decodes with — a capture artifact, not a behaviour
    // difference (CandidatesToolTests/QueryToolTests document the same substitution).
    private static string Normalize(string s) => s.Trim().Replace('—', '-');
}
