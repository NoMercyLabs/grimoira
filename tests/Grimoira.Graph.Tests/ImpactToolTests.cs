using System.Reflection;
using Grimoira.Graph.Tools;
using Grimoira.Store.Data;
using Grimoira.TestSupport;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Grimoira.Graph.Tests;

public class ImpactToolTests
{
    [Fact]
    public void CliShapeMatchesTodaysCliOutputForARecordedSymbol()
    {
        string oldInstance = GrimoiraCliRunner.NewTestInstance("impact-cli-old");
        string newInstance = GrimoiraCliRunner.NewTestInstance("impact-cli-new");
        try
        {
            // Oracle: today's grimoira.cs ImpactCmd() (grimoira.cs:2482).
            string oldDb = GrimoiraCliRunner.InstanceDbPath(oldInstance);
            GrimoiraCliRunner.Run($"init --instance {oldInstance}");
            SeedEdge(oldDb, "has_more", "web", "src/list.ts", 1, "page.has_more", "PaginatedResponse");
            SeedEdge(oldDb, "has_more", "api", "src/dto.ts", 2, "", "PaginatedResponse");
            (string stdout, int exitCode) = GrimoiraCliRunner.Run($"impact --instance {oldInstance} has_more");
            Assert.Equal(0, exitCode);
            string expected = stdout.Trim();

            // New: ImpactTool.
            string newDb = GrimoiraCliRunner.InstanceDbPath(newInstance);
            GrimoiraCliRunner.Run($"init --instance {newInstance}");
            SeedEdge(newDb, "has_more", "web", "src/list.ts", 1, "page.has_more", "PaginatedResponse");
            SeedEdge(newDb, "has_more", "api", "src/dto.ts", 2, "", "PaginatedResponse");

            using SqliteConnection connection = StoreConnection.Open(newDb);
            string actual = new ImpactTool().ExecuteCli(connection, "has_more").Trim();

            Assert.Equal(expected, actual);
            Assert.Contains("1 consumer(s) to review", actual);
            Assert.Contains("1 contract site(s)", actual);
        }
        finally
        {
            GrimoiraCliRunner.DeleteInstance(oldInstance);
            GrimoiraCliRunner.DeleteInstance(newInstance);
        }
    }

    [Fact]
    public void CliShapeReportsNoRecordedConsumersForAnUnknownSymbol()
    {
        string instance = GrimoiraCliRunner.NewTestInstance("impact-cli-unknown");
        try
        {
            GrimoiraCliRunner.Run($"init --instance {instance}");

            (string stdout, int exitCode) = GrimoiraCliRunner.Run($"impact --instance {instance} never-indexed-symbol");
            Assert.Equal(0, exitCode);
            string expected = stdout.Trim();

            string dbPath = GrimoiraCliRunner.InstanceDbPath(instance);
            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = new ImpactTool().ExecuteCli(connection, "never-indexed-symbol").Trim();

            Assert.Equal(expected, actual);
            Assert.Equal("'never-indexed-symbol' has no recorded consumers (safe to change, or not yet indexed).", actual);
        }
        finally
        {
            GrimoiraCliRunner.DeleteInstance(instance);
        }
    }

    // mcp.cs's `impact` resolves its own connection from GRIMOIRA_INSTANCE (same pattern as
    // HistoryToolTests' McpShapeMatchesTodaysMcpOutput / QueryToolTests' InvokeMcpFact).
    [Fact]
    public void McpShapeMatchesTodaysMcpOutputForARecordedSymbol()
    {
        string instance = GrimoiraCliRunner.NewTestInstance("impact-mcp-hit");
        string? previousInstanceEnv = Environment.GetEnvironmentVariable("GRIMOIRA_INSTANCE");
        try
        {
            string dbPath = GrimoiraCliRunner.InstanceDbPath(instance);
            GrimoiraCliRunner.Run($"init --instance {instance}");
            SeedEdge(dbPath, "has_more_mcp", "web", "src/list.ts", 1, "page.has_more_mcp", "PaginatedResponse", hardcoded: 1);
            SeedEdge(dbPath, "has_more_mcp", "api", "src/dto.ts", 2, "", "PaginatedResponse", hardcoded: 0);

            Environment.SetEnvironmentVariable("GRIMOIRA_INSTANCE", instance);
            string expected = (string)InvokeMcpImpact("has_more_mcp")!;

            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = new ImpactTool().ExecuteMcp(connection, "has_more_mcp");

            Assert.Equal(expected, actual);
            Assert.Contains("1 hardcoded", actual);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GRIMOIRA_INSTANCE", previousInstanceEnv);
            GrimoiraCliRunner.DeleteInstance(instance);
        }
    }

    [Fact]
    public void McpShapeReportsNoRecordedConsumersForAnUnknownSymbol()
    {
        string instance = GrimoiraCliRunner.NewTestInstance("impact-mcp-unknown");
        string? previousInstanceEnv = Environment.GetEnvironmentVariable("GRIMOIRA_INSTANCE");
        try
        {
            GrimoiraCliRunner.Run($"init --instance {instance}");

            Environment.SetEnvironmentVariable("GRIMOIRA_INSTANCE", instance);
            string expected = (string)InvokeMcpImpact("never-indexed-mcp-symbol")!;

            string dbPath = GrimoiraCliRunner.InstanceDbPath(instance);
            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = new ImpactTool().ExecuteMcp(connection, "never-indexed-mcp-symbol");

            Assert.Equal(expected, actual);
            Assert.Equal("'never-indexed-mcp-symbol' has no recorded consumers (safe to change, or not yet indexed).", actual);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GRIMOIRA_INSTANCE", previousInstanceEnv);
            GrimoiraCliRunner.DeleteInstance(instance);
        }
    }

    private static void SeedEdge(string dbPath, string symbol, string project, string file, int line, string usage, string contract, int hardcoded = 0)
    {
        using SqliteConnection connection = StoreConnection.Open(dbPath);
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

    private static object? InvokeMcpImpact(string symbol)
    {
        Assembly mcp = Assembly.LoadFrom(FindMcpDll());
        Type tools = mcp.GetType("GrimoiraTools") ?? throw new InvalidOperationException("GrimoiraTools type not found in mcp.dll");
        MethodInfo method = tools.GetMethod("impact", BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException("GrimoiraTools.impact not found in mcp.dll");
        return method.Invoke(null, [symbol]);
    }

    private static string FindMcpDll()
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
