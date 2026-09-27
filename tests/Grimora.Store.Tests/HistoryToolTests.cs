using System.Reflection;
using Grimora.Store.Data;
using Grimora.TestSupport;
using Grimora.Store.Tools;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Grimora.Store.Tests;

public class HistoryToolTests
{
    [Fact]
    public void CliShapeMatchesTodaysCliOutput()
    {
        string instance = GrimoraCliRunner.NewTestInstance("history-cli");
        try
        {
            GrimoraCliRunner.Run($"init --instance {instance}");
            GrimoraCliRunner.Run($"add --instance {instance} --term history-fixture --value one --category manual");
            GrimoraCliRunner.Run($"add --instance {instance} --term history-fixture --value two --category manual");

            (string stdout, int exitCode) = GrimoraCliRunner.Run($"history --instance {instance} history-fixture");
            Assert.Equal(0, exitCode);
            string expected = Normalize(stdout);

            string dbPath = GrimoraCliRunner.InstanceDbPath(instance);
            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = Normalize(new HistoryTool().ExecuteCli(connection, "history-fixture"));

            Assert.Equal(expected, actual);
            Assert.Contains("mutation(s) in the cold log", actual);
        }
        finally
        {
            GrimoraCliRunner.DeleteInstance(instance);
        }
    }

    [Fact]
    public void CliShapeReportsNoHistoryForAnUnknownTerm()
    {
        string instance = GrimoraCliRunner.NewTestInstance("history-cli-empty");
        try
        {
            GrimoraCliRunner.Run($"init --instance {instance}");

            (string stdout, int exitCode) = GrimoraCliRunner.Run($"history --instance {instance} nothing-ever-named-this");
            Assert.Equal(0, exitCode);
            string expected = Normalize(stdout);

            string dbPath = GrimoraCliRunner.InstanceDbPath(instance);
            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = Normalize(new HistoryTool().ExecuteCli(connection, "nothing-ever-named-this"));

            Assert.Equal(expected, actual);
        }
        finally
        {
            GrimoraCliRunner.DeleteInstance(instance);
        }
    }

    // mcp.cs's `history` (mcp.cs:871-882) resolves its own connection from Grimora_INSTANCE, so the oracle
    // call and the new tool's call both go through the same env var, never in parallel with another
    // test that touches it (xunit runs test *methods* within a class sequentially by default; this is
    // the only class in this assembly that mutates Grimora_INSTANCE).
    [Fact]
    public void McpShapeMatchesTodaysMcpOutput()
    {
        string instance = GrimoraCliRunner.NewTestInstance("history-mcp");
        string? previousInstanceEnv = Environment.GetEnvironmentVariable("Grimora_INSTANCE");
        try
        {
            GrimoraCliRunner.Run($"init --instance {instance}");
            GrimoraCliRunner.Run($"add --instance {instance} --term history-fixture-mcp --value one --category manual");
            GrimoraCliRunner.Run($"add --instance {instance} --term history-fixture-mcp --value two --category manual");

            Environment.SetEnvironmentVariable("Grimora_INSTANCE", instance);
            string expected = (string)InvokeMcpHistory("history-fixture-mcp")!;

            string dbPath = GrimoraCliRunner.InstanceDbPath(instance);
            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = new HistoryTool().ExecuteMcp(connection, "history-fixture-mcp");

            Assert.Equal(expected, actual);
            Assert.Contains("history-fixture-mcp", actual);
        }
        finally
        {
            Environment.SetEnvironmentVariable("Grimora_INSTANCE", previousInstanceEnv);
            GrimoraCliRunner.DeleteInstance(instance);
        }
    }

    private static object? InvokeMcpHistory(string term)
    {
        Assembly mcp = Assembly.LoadFrom(FindMcpDll());
        Type tools = mcp.GetType("GrimoraTools") ?? throw new InvalidOperationException("GrimoraTools type not found in mcp.dll");
        MethodInfo method = tools.GetMethod("history", BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException("GrimoraTools.history not found in mcp.dll");
        return method.Invoke(null, [term]);
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

    private static string Normalize(string s) => s.Replace("\r\n", "\n").Trim();
}
