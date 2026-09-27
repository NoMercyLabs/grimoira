using System.Reflection;
using Grimora.Graph.Tools;
using Grimora.Store.Data;
using Grimora.TestSupport;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Grimora.Graph.Tests;

// Ports mcp-graph.test.mjs's graph_query case (McpFixtureWidget) plus the CLI oracle pin, the same
// pattern ImpactToolTests/QueryToolTests use.
public class GraphQueryToolTests
{
    [Fact]
    public void CliShapeMatchesTodaysCliOutputForAMatchingQuestion()
    {
        string oldInstance = GrimoraCliRunner.NewTestInstance("graph-query-cli-old");
        string newInstance = GrimoraCliRunner.NewTestInstance("graph-query-cli-new");
        try
        {
            // Oracle: today's grimora.cs GraphQueryCmd() (grimora.cs:2619).
            string oldDb = GrimoraCliRunner.InstanceDbPath(oldInstance);
            GrimoraCliRunner.Run($"init --instance {oldInstance}");
            SeedFixture(oldDb);
            (string stdout, int exitCode) = GrimoraCliRunner.Run($"graph-query --instance {oldInstance} GraphFixtureWidget");
            Assert.Equal(0, exitCode);
            string expected = stdout.Trim();

            string newDb = GrimoraCliRunner.InstanceDbPath(newInstance);
            GrimoraCliRunner.Run($"init --instance {newInstance}");
            SeedFixture(newDb);

            using SqliteConnection connection = StoreConnection.Open(newDb);
            string actual = new GraphQueryTool().ExecuteCli(connection, "GraphFixtureWidget").Trim();

            Assert.Equal(expected, actual);
            Assert.Contains("GraphFixtureWidget", actual);
            Assert.Contains("[alpha]", actual);
        }
        finally
        {
            GrimoraCliRunner.DeleteInstance(oldInstance);
            GrimoraCliRunner.DeleteInstance(newInstance);
        }
    }

    [Fact]
    public void CliShapeReportsNoSymbolMatchForAnUnmatchedQuestion()
    {
        string instance = GrimoraCliRunner.NewTestInstance("graph-query-cli-miss");
        try
        {
            GrimoraCliRunner.Run($"init --instance {instance}");

            (string stdout, int exitCode) = GrimoraCliRunner.Run($"graph-query --instance {instance} nosuchsymbolatallever");
            Assert.Equal(0, exitCode);
            string expected = stdout.Trim();

            string dbPath = GrimoraCliRunner.InstanceDbPath(instance);
            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = new GraphQueryTool().ExecuteCli(connection, "nosuchsymbolatallever").Trim();

            Assert.Equal(expected, actual);
            Assert.Contains("no symbol matches", actual);
        }
        finally
        {
            GrimoraCliRunner.DeleteInstance(instance);
        }
    }

    // Ported from mcp-graph.test.mjs: 'graph_query: finds the fixture symbol' / 'groups it under its
    // home project'. mcp.cs's graph_query resolves its own connection from Grimora_INSTANCE.
    [Fact]
    public void McpShapeMatchesTodaysMcpOutputForAMatchingQuestion()
    {
        string instance = GrimoraCliRunner.NewTestInstance("graph-query-mcp-hit");
        string? previousInstanceEnv = Environment.GetEnvironmentVariable("Grimora_INSTANCE");
        try
        {
            string dbPath = GrimoraCliRunner.InstanceDbPath(instance);
            GrimoraCliRunner.Run($"init --instance {instance}");
            SeedFixture(dbPath);

            Environment.SetEnvironmentVariable("Grimora_INSTANCE", instance);
            string expected = (string)InvokeMcp("graph_query", "GraphFixtureWidget")!;

            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = new GraphQueryTool().ExecuteMcp(connection, "GraphFixtureWidget");

            Assert.Equal(expected, actual);
            Assert.Contains("GraphFixtureWidget", actual);
            Assert.Contains("[alpha]", actual);
        }
        finally
        {
            Environment.SetEnvironmentVariable("Grimora_INSTANCE", previousInstanceEnv);
            GrimoraCliRunner.DeleteInstance(instance);
        }
    }

    private static void SeedFixture(string dbPath)
    {
        using SqliteConnection connection = StoreConnection.Open(dbPath);
        InsertEdge(connection, "GraphFixtureWidget", "decl", "alpha", "alpha/widget.ts", 10, "ts declaration");
        InsertEdge(connection, "GraphFixtureWidget", "", "beta", "beta/x.ts", 5, "uses widget");
        InsertEdge(connection, "GraphFixtureWidget", "", "beta", "beta/z.ts", 1, "uses widget again");
        InsertEdge(connection, "GraphFixtureWidget", "", "gamma", "gamma/y.ts", 9, "uses widget too");
    }

    internal static void InsertEdge(SqliteConnection connection, string symbol, string contract, string project, string file, int line, string usage, int hardcoded = 0)
    {
        using SqliteCommand insert = connection.CreateCommand();
        insert.CommandText = "INSERT INTO edges(symbol,contract,project,file,line,usage,hardcoded) VALUES($s,$c,$p,$f,$l,$u,$h)";
        insert.Parameters.AddWithValue("$s", symbol);
        insert.Parameters.AddWithValue("$c", contract);
        insert.Parameters.AddWithValue("$p", project);
        insert.Parameters.AddWithValue("$f", file);
        insert.Parameters.AddWithValue("$l", line);
        insert.Parameters.AddWithValue("$u", usage);
        insert.Parameters.AddWithValue("$h", hardcoded);
        insert.ExecuteNonQuery();
    }

    internal static object? InvokeMcp(string toolName, params object[] args)
    {
        Assembly mcp = Assembly.LoadFrom(FindMcpDll());
        Type tools = mcp.GetType("GrimoraTools") ?? throw new InvalidOperationException("GrimoraTools type not found in mcp.dll");
        MethodInfo method = tools.GetMethod(toolName, BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException($"GrimoraTools.{toolName} not found in mcp.dll");
        return method.Invoke(null, args);
    }

    internal static string FindMcpDll()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "bin", "mcp.dll");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new InvalidOperationException(
            $"bin/mcp.dll not found above {AppContext.BaseDirectory} — run build-mcp.ps1 first");
    }
}
