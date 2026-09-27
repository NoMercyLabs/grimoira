using Grimora.Graph.Tools;
using Grimora.Store.Data;
using Grimora.TestSupport;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Grimora.Graph.Tests;

// Ports mcp-graph.test.mjs's graph_explain case (declaration site, users grouped by project) plus the
// CLI oracle pin from grimora.cs's own selftest fixture (grimora.cs:3290-3325).
public class GraphExplainToolTests
{
    [Fact]
    public void CliShapeMatchesTodaysCliOutputForARecordedSymbol()
    {
        string oldInstance = GrimoraCliRunner.NewTestInstance("graph-explain-cli-old");
        string newInstance = GrimoraCliRunner.NewTestInstance("graph-explain-cli-new");
        try
        {
            // Oracle: today's grimora.cs GraphExplainCmd() (grimora.cs:2733).
            string oldDb = GrimoraCliRunner.InstanceDbPath(oldInstance);
            GrimoraCliRunner.Run($"init --instance {oldInstance}");
            SeedFixture(oldDb);
            (string stdout, int exitCode) = GrimoraCliRunner.Run($"graph-explain --instance {oldInstance} GraphFixtureWidget");
            Assert.Equal(0, exitCode);
            string expected = Normalize(stdout);

            string newDb = GrimoraCliRunner.InstanceDbPath(newInstance);
            GrimoraCliRunner.Run($"init --instance {newInstance}");
            SeedFixture(newDb);

            using SqliteConnection connection = StoreConnection.Open(newDb);
            string actual = Normalize(new GraphExplainTool().ExecuteCli(connection, "GraphFixtureWidget"));

            Assert.Equal(expected, actual);
            Assert.Contains("ts declaration", actual);
            Assert.Contains("alpha/widget.ts:10", actual);
        }
        finally
        {
            GrimoraCliRunner.DeleteInstance(oldInstance);
            GrimoraCliRunner.DeleteInstance(newInstance);
        }
    }

    [Fact]
    public void CliShapeReportsNoSymbolMatchForAnUnknownSymbol()
    {
        string instance = GrimoraCliRunner.NewTestInstance("graph-explain-cli-unknown");
        try
        {
            GrimoraCliRunner.Run($"init --instance {instance}");

            (string stdout, int exitCode) = GrimoraCliRunner.Run($"graph-explain --instance {instance} never-indexed-symbol");
            Assert.Equal(0, exitCode);
            string expected = stdout.Trim();

            string dbPath = GrimoraCliRunner.InstanceDbPath(instance);
            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = new GraphExplainTool().ExecuteCli(connection, "never-indexed-symbol").Trim();

            Assert.Equal(expected, actual);
            Assert.Contains("no symbol matches", actual);
        }
        finally
        {
            GrimoraCliRunner.DeleteInstance(instance);
        }
    }

    // Ported from mcp-graph.test.mjs: 'graph_explain: shows the declaration site' / 'lists users grouped
    // by project'. mcp.cs's graph_explain resolves its own connection from GRIMORA_INSTANCE.
    [Fact]
    public void McpShapeMatchesTodaysMcpOutputForARecordedSymbol()
    {
        string instance = GrimoraCliRunner.NewTestInstance("graph-explain-mcp-hit");
        string? previousInstanceEnv = Environment.GetEnvironmentVariable("GRIMORA_INSTANCE");
        try
        {
            string dbPath = GrimoraCliRunner.InstanceDbPath(instance);
            GrimoraCliRunner.Run($"init --instance {instance}");
            SeedFixture(dbPath);

            Environment.SetEnvironmentVariable("GRIMORA_INSTANCE", instance);
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
            Environment.SetEnvironmentVariable("GRIMORA_INSTANCE", previousInstanceEnv);
            GrimoraCliRunner.DeleteInstance(instance);
        }
    }

    [Fact]
    public void McpShapeReportsNoSymbolMatchForAnUnknownSymbol()
    {
        string instance = GrimoraCliRunner.NewTestInstance("graph-explain-mcp-unknown");
        string? previousInstanceEnv = Environment.GetEnvironmentVariable("GRIMORA_INSTANCE");
        try
        {
            GrimoraCliRunner.Run($"init --instance {instance}");

            Environment.SetEnvironmentVariable("GRIMORA_INSTANCE", instance);
            string expected = (string)GraphQueryToolTests.InvokeMcp("graph_explain", "never-indexed-mcp-symbol")!;

            string dbPath = GrimoraCliRunner.InstanceDbPath(instance);
            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = new GraphExplainTool().ExecuteMcp(connection, "never-indexed-mcp-symbol");

            Assert.Equal(expected, actual);
            Assert.Contains("no symbol matches", actual);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GRIMORA_INSTANCE", previousInstanceEnv);
            GrimoraCliRunner.DeleteInstance(instance);
        }
    }

    // Known-issue fix (RESTRUCTURE.md slice 13): "used by" must count only usage sites, not a symbol's
    // own declaration row. GraphOnlyDeclaredWidget is declared once in "alpha" and never used anywhere
    // else — "alpha" must not be reported as a "used by" project at all, and the total site count must
    // be 0, not 1 for the phantom declaration-as-use.
    [Fact]
    public void UsedByExcludesTheSymbolsOwnDeclarationSite()
    {
        string instance = GrimoraCliRunner.NewTestInstance("graph-explain-known-issue");
        try
        {
            string dbPath = GrimoraCliRunner.InstanceDbPath(instance);
            GrimoraCliRunner.Run($"init --instance {instance}");
            using (SqliteConnection connection = StoreConnection.Open(dbPath))
            {
                GraphQueryToolTests.InsertEdge(connection, "GraphOnlyDeclaredWidget", "decl", "alpha", "alpha/widget.ts", 10, "ts declaration");
            }

            using SqliteConnection readConnection = StoreConnection.Open(dbPath);
            string actual = new GraphExplainTool().ExecuteCli(readConnection, "GraphOnlyDeclaredWidget");

            Assert.Contains("used by (0 site(s) across 0 project(s)):", actual);
            int usedByStart = actual.IndexOf("used by", StringComparison.Ordinal);
            int topSitesStart = actual.IndexOf("top sites:", StringComparison.Ordinal);
            string usedBySection = actual[usedByStart..topSitesStart];
            Assert.DoesNotContain("alpha", usedBySection);
        }
        finally
        {
            GrimoraCliRunner.DeleteInstance(instance);
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

    // Windows redirects the child's stdout through the OEM codepage, not UTF-8, so grimora.cs's "—" arrives
    // mangled regardless of the encoding this side decodes with — a capture artifact, not a behaviour
    // difference (CandidatesToolTests/QueryToolTests document the same substitution).
    private static string Normalize(string s) => s.Trim().Replace('—', '-');
}
