using Grimora.Graph.Tools;
using Grimora.Store.Data;
using Grimora.TestSupport;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Grimora.Graph.Tests;

// Ports mcp-graph.test.mjs's graph_path cases (2-hop path, unresolvable symbol) plus the CLI oracle pin.
public class GraphPathToolTests
{
    [Fact]
    public void CliShapeFindsTheTwoHopPath()
    {
        string oldInstance = GrimoraCliRunner.NewTestInstance("graph-path-cli-old");
        string newInstance = GrimoraCliRunner.NewTestInstance("graph-path-cli-new");
        try
        {
            // Oracle: today's grimora.cs GraphPathCmd()/GraphBfs() (grimora.cs:2706, grimora.cs:2558).
            string oldDb = GrimoraCliRunner.InstanceDbPath(oldInstance);
            GrimoraCliRunner.Run($"init --instance {oldInstance}");
            SeedPathFixture(oldDb);
            (string stdout, int exitCode) = GrimoraCliRunner.Run($"graph-path --instance {oldInstance} GraphPathFixtureA GraphPathFixtureB");
            Assert.Equal(0, exitCode);
            string expected = stdout.Trim();

            string newDb = GrimoraCliRunner.InstanceDbPath(newInstance);
            GrimoraCliRunner.Run($"init --instance {newInstance}");
            SeedPathFixture(newDb);

            using SqliteConnection connection = StoreConnection.Open(newDb);
            string actual = new GraphPathTool().ExecuteCli(connection, "GraphPathFixtureA", "GraphPathFixtureB").Trim();

            Assert.Equal(expected, actual);
            Assert.Contains("(2 hop(s))", actual);
        }
        finally
        {
            GrimoraCliRunner.DeleteInstance(oldInstance);
            GrimoraCliRunner.DeleteInstance(newInstance);
        }
    }

    [Fact]
    public void CliShapeRefusesCleanlyForAnUnresolvableSymbol()
    {
        string instance = GrimoraCliRunner.NewTestInstance("graph-path-cli-miss");
        try
        {
            GrimoraCliRunner.Run($"init --instance {instance}");
            SeedPathFixture(GrimoraCliRunner.InstanceDbPath(instance));

            (string stdout, int exitCode) = GrimoraCliRunner.Run($"graph-path --instance {instance} NoSuchSymbolAtAll GraphPathFixtureA");
            Assert.Equal(0, exitCode);
            string expected = stdout.Trim();

            string dbPath = GrimoraCliRunner.InstanceDbPath(instance);
            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = new GraphPathTool().ExecuteCli(connection, "NoSuchSymbolAtAll", "GraphPathFixtureA").Trim();

            Assert.Equal(expected, actual);
            Assert.Contains("no symbol or file matches", actual);
        }
        finally
        {
            GrimoraCliRunner.DeleteInstance(instance);
        }
    }

    // Ported from mcp-graph.test.mjs: 'graph_path: finds the 2-hop path' / 'unresolvable symbol refuses
    // cleanly'. mcp.cs's graph_path resolves its own connection from GRIMORA_INSTANCE.
    [Fact]
    public void McpShapeMatchesTodaysMcpOutputForTheTwoHopPath()
    {
        string instance = GrimoraCliRunner.NewTestInstance("graph-path-mcp-hit");
        string? previousInstanceEnv = Environment.GetEnvironmentVariable("GRIMORA_INSTANCE");
        try
        {
            string dbPath = GrimoraCliRunner.InstanceDbPath(instance);
            GrimoraCliRunner.Run($"init --instance {instance}");
            SeedPathFixture(dbPath);

            Environment.SetEnvironmentVariable("GRIMORA_INSTANCE", instance);
            string expected = (string)GraphQueryToolTests.InvokeMcp("graph_path", "GraphPathFixtureA", "GraphPathFixtureB")!;

            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = new GraphPathTool().ExecuteMcp(connection, "GraphPathFixtureA", "GraphPathFixtureB");

            Assert.Equal(expected, actual);
            Assert.Contains("(2 hop(s))", actual);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GRIMORA_INSTANCE", previousInstanceEnv);
            GrimoraCliRunner.DeleteInstance(instance);
        }
    }

    [Fact]
    public void McpShapeRefusesCleanlyForAnUnresolvableSymbol()
    {
        string instance = GrimoraCliRunner.NewTestInstance("graph-path-mcp-miss");
        string? previousInstanceEnv = Environment.GetEnvironmentVariable("GRIMORA_INSTANCE");
        try
        {
            string dbPath = GrimoraCliRunner.InstanceDbPath(instance);
            GrimoraCliRunner.Run($"init --instance {instance}");
            SeedPathFixture(dbPath);

            Environment.SetEnvironmentVariable("GRIMORA_INSTANCE", instance);
            string expected = (string)GraphQueryToolTests.InvokeMcp("graph_path", "NoSuchSymbolEver", "GraphPathFixtureA")!;

            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = new GraphPathTool().ExecuteMcp(connection, "NoSuchSymbolEver", "GraphPathFixtureA");

            Assert.Equal(expected, actual);
            Assert.Contains("no symbol or file matches", actual);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GRIMORA_INSTANCE", previousInstanceEnv);
            GrimoraCliRunner.DeleteInstance(instance);
        }
    }

    // RESTRUCTURE.md slice 24 bullet 1: ports the 2 `selftest` checks GraphPathToolTests didn't yet cover
    // (grimora.cs's old SelfTest: "graph-path: reports no path past depth 6" / "graph-path: same node
    // short-circuits") now that selftest itself is gone.
    [Fact]
    public void ReportsNoPathPastDepthSix()
    {
        string instance = GrimoraCliRunner.NewTestInstance("graph-path-depth-cutoff");
        try
        {
            GrimoraCliRunner.Run($"init --instance {instance}");
            string dbPath = GrimoraCliRunner.InstanceDbPath(instance);
            using (SqliteConnection connection = StoreConnection.Open(dbPath))
            {
                // A chain 8 hops long (sym0-f0-sym1-f1-sym2-f2-sym3-f3-sym4): past the depth-6 cutoff on purpose.
                GraphQueryToolTests.InsertEdge(connection, "ChainSym0", "decl", "chain", "chain/f0.ts", 1, "ts declaration");
                GraphQueryToolTests.InsertEdge(connection, "ChainSym1", "decl", "chain", "chain/f0.ts", 2, "ts declaration");
                GraphQueryToolTests.InsertEdge(connection, "ChainSym1", "", "chain", "chain/f1.ts", 1, "uses");
                GraphQueryToolTests.InsertEdge(connection, "ChainSym2", "decl", "chain", "chain/f1.ts", 2, "ts declaration");
                GraphQueryToolTests.InsertEdge(connection, "ChainSym2", "", "chain", "chain/f2.ts", 1, "uses");
                GraphQueryToolTests.InsertEdge(connection, "ChainSym3", "decl", "chain", "chain/f2.ts", 2, "ts declaration");
                GraphQueryToolTests.InsertEdge(connection, "ChainSym3", "", "chain", "chain/f3.ts", 1, "uses");
                GraphQueryToolTests.InsertEdge(connection, "ChainSym4", "decl", "chain", "chain/f3.ts", 2, "ts declaration");
            }

            using SqliteConnection readConnection = StoreConnection.Open(dbPath);
            string result = new GraphPathTool().ExecuteCli(readConnection, "ChainSym0", "ChainSym4");

            Assert.Contains("no path found", result);
            Assert.Contains("depth 6", result);
        }
        finally
        {
            GrimoraCliRunner.DeleteInstance(instance);
        }
    }

    [Fact]
    public void SameNodeShortCircuitsWithoutSearching()
    {
        string instance = GrimoraCliRunner.NewTestInstance("graph-path-same-node");
        try
        {
            GrimoraCliRunner.Run($"init --instance {instance}");
            string dbPath = GrimoraCliRunner.InstanceDbPath(instance);
            SeedFixtureForSameNode(dbPath);

            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string result = new GraphPathTool().ExecuteCli(connection, "GraphFixtureWidget", "GraphFixtureWidget");

            Assert.Equal("same node.", result);
        }
        finally
        {
            GrimoraCliRunner.DeleteInstance(instance);
        }
    }

    private static void SeedFixtureForSameNode(string dbPath)
    {
        using SqliteConnection connection = StoreConnection.Open(dbPath);
        GraphQueryToolTests.InsertEdge(connection, "GraphFixtureWidget", "decl", "alpha", "alpha/widget.ts", 10, "ts declaration");
    }

    private static void SeedPathFixture(string dbPath)
    {
        using SqliteConnection connection = StoreConnection.Open(dbPath);
        GraphQueryToolTests.InsertEdge(connection, "GraphPathFixtureA", "decl", "alpha", "pathfixture/shared.ts", 1, "ts declaration");
        GraphQueryToolTests.InsertEdge(connection, "GraphPathFixtureB", "decl", "alpha", "pathfixture/shared.ts", 2, "ts declaration");
    }
}
