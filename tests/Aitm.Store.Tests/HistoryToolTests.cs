using System.Reflection;
using Aitm.Store.Data;
using Aitm.Store.Tests.Support;
using Aitm.Store.Tools;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Aitm.Store.Tests;

public class HistoryToolTests
{
    [Fact]
    public void CliShapeMatchesTodaysCliOutput()
    {
        string instance = AitmCliRunner.NewTestInstance("history-cli");
        try
        {
            AitmCliRunner.Run($"init --instance {instance}");
            AitmCliRunner.Run($"add --instance {instance} --term history-fixture --value one --category manual");
            AitmCliRunner.Run($"add --instance {instance} --term history-fixture --value two --category manual");

            (string stdout, int exitCode) = AitmCliRunner.Run($"history --instance {instance} history-fixture");
            Assert.Equal(0, exitCode);
            string expected = Normalize(stdout);

            string dbPath = AitmCliRunner.InstanceDbPath(instance);
            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = Normalize(new HistoryTool().ExecuteCli(connection, "history-fixture"));

            Assert.Equal(expected, actual);
            Assert.Contains("mutation(s) in the cold log", actual);
        }
        finally
        {
            AitmCliRunner.DeleteInstance(instance);
        }
    }

    [Fact]
    public void CliShapeReportsNoHistoryForAnUnknownTerm()
    {
        string instance = AitmCliRunner.NewTestInstance("history-cli-empty");
        try
        {
            AitmCliRunner.Run($"init --instance {instance}");

            (string stdout, int exitCode) = AitmCliRunner.Run($"history --instance {instance} nothing-ever-named-this");
            Assert.Equal(0, exitCode);
            string expected = Normalize(stdout);

            string dbPath = AitmCliRunner.InstanceDbPath(instance);
            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = Normalize(new HistoryTool().ExecuteCli(connection, "nothing-ever-named-this"));

            Assert.Equal(expected, actual);
        }
        finally
        {
            AitmCliRunner.DeleteInstance(instance);
        }
    }

    // mcp.cs's `history` (mcp.cs:871-882) resolves its own connection from AITM_INSTANCE, so the oracle
    // call and the new tool's call both go through the same env var, never in parallel with another
    // test that touches it (xunit runs test *methods* within a class sequentially by default; this is
    // the only class in this assembly that mutates AITM_INSTANCE).
    [Fact]
    public void McpShapeMatchesTodaysMcpOutput()
    {
        string instance = AitmCliRunner.NewTestInstance("history-mcp");
        string? previousInstanceEnv = Environment.GetEnvironmentVariable("AITM_INSTANCE");
        try
        {
            AitmCliRunner.Run($"init --instance {instance}");
            AitmCliRunner.Run($"add --instance {instance} --term history-fixture-mcp --value one --category manual");
            AitmCliRunner.Run($"add --instance {instance} --term history-fixture-mcp --value two --category manual");

            Environment.SetEnvironmentVariable("AITM_INSTANCE", instance);
            string expected = (string)InvokeMcpHistory("history-fixture-mcp")!;

            string dbPath = AitmCliRunner.InstanceDbPath(instance);
            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = new HistoryTool().ExecuteMcp(connection, "history-fixture-mcp");

            Assert.Equal(expected, actual);
            Assert.Contains("history-fixture-mcp", actual);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AITM_INSTANCE", previousInstanceEnv);
            AitmCliRunner.DeleteInstance(instance);
        }
    }

    private static object? InvokeMcpHistory(string term)
    {
        Assembly mcp = Assembly.LoadFrom(FindMcpDll());
        Type tools = mcp.GetType("AitmTools") ?? throw new InvalidOperationException("AitmTools type not found in mcp.dll");
        MethodInfo method = tools.GetMethod("history", BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException("AitmTools.history not found in mcp.dll");
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
