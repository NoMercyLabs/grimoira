using System.Reflection;
using Aitm.Facts.Tests.Support;
using Aitm.Facts.Tools;
using Aitm.Store.Data;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Aitm.Facts.Tests;

public class QueryToolTests
{
    [Fact]
    public void CliShapeMatchesTodaysCliOutputForAConfidentMatch()
    {
        string instance = AitmCliRunner.NewTestInstance("query-cli-hit");
        try
        {
            AitmCliRunner.Run($"init --instance {instance}");
            AitmCliRunner.Run($"add --instance {instance} --term query-fixture --value the-answer --category manual");

            (string stdout, int exitCode) = AitmCliRunner.Run($"query --instance {instance} query-fixture");
            Assert.Equal(0, exitCode);
            string expected = Normalize(stdout);

            string dbPath = AitmCliRunner.InstanceDbPath(instance);
            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = Normalize(new QueryTool(new UsageSignal()).ExecuteCli(connection, "query-fixture"));

            Assert.Equal(StripTiming(expected), StripTiming(actual));
            Assert.Contains("the-answer", actual);
        }
        finally
        {
            AitmCliRunner.DeleteInstance(instance);
        }
    }

    [Fact]
    public void CliShapeReportsNoConfidentAnswerForAGapQuery()
    {
        string instance = AitmCliRunner.NewTestInstance("query-cli-gap");
        try
        {
            AitmCliRunner.Run($"init --instance {instance}");

            (string stdout, int exitCode) = AitmCliRunner.Run($"query --instance {instance} nothing-ever-matches-this-term");
            Assert.Equal(0, exitCode);
            string expected = Normalize(stdout);

            string dbPath = AitmCliRunner.InstanceDbPath(instance);
            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = Normalize(new QueryTool(new UsageSignal()).ExecuteCli(connection, "nothing-ever-matches-this-term"));

            Assert.Equal(StripTiming(expected), StripTiming(actual));
            Assert.Contains("gap logged", actual);
        }
        finally
        {
            AitmCliRunner.DeleteInstance(instance);
        }
    }

    // mcp.cs's `fact` resolves its own connection from AITM_INSTANCE (same pattern as HistoryToolTests'
    // McpShapeMatchesTodaysMcpOutput).
    [Fact]
    public void McpShapeMatchesTodaysMcpOutputForAConfidentMatch()
    {
        string instance = AitmCliRunner.NewTestInstance("query-mcp-hit");
        string? previousInstanceEnv = Environment.GetEnvironmentVariable("AITM_INSTANCE");
        try
        {
            AitmCliRunner.Run($"init --instance {instance}");
            AitmCliRunner.Run($"add --instance {instance} --term queryfixturemcp --value the-mcp-answer --category manual");

            Environment.SetEnvironmentVariable("AITM_INSTANCE", instance);
            string expected = (string)InvokeMcpFact("queryfixturemcp")!;

            string dbPath = AitmCliRunner.InstanceDbPath(instance);
            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = new QueryTool(new UsageSignal()).ExecuteMcp(connection, "queryfixturemcp");

            Assert.Equal(expected, actual);
            Assert.Contains("the-mcp-answer", actual);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AITM_INSTANCE", previousInstanceEnv);
            AitmCliRunner.DeleteInstance(instance);
        }
    }

    [Fact]
    public void McpShapeReportsGapSuffixWhenNoMatch()
    {
        string instance = AitmCliRunner.NewTestInstance("query-mcp-gap");
        string? previousInstanceEnv = Environment.GetEnvironmentVariable("AITM_INSTANCE");
        try
        {
            AitmCliRunner.Run($"init --instance {instance}");

            Environment.SetEnvironmentVariable("AITM_INSTANCE", instance);
            string expected = (string)InvokeMcpFact("nothing-ever-matches-this-mcp-term")!;

            string dbPath = AitmCliRunner.InstanceDbPath(instance);
            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = new QueryTool(new UsageSignal()).ExecuteMcp(connection, "nothing-ever-matches-this-mcp-term");

            Assert.Equal(expected, actual);
            Assert.Contains("gap logged", actual);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AITM_INSTANCE", previousInstanceEnv);
            AitmCliRunner.DeleteInstance(instance);
        }
    }

    private static object? InvokeMcpFact(string query)
    {
        Assembly mcp = Assembly.LoadFrom(FindMcpDll());
        Type tools = mcp.GetType("AitmTools") ?? throw new InvalidOperationException("AitmTools type not found in mcp.dll");
        MethodInfo method = tools.GetMethod("fact", BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException("AitmTools.fact not found in mcp.dll");
        return method.Invoke(null, [query]);
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

    // Windows redirects the child's stdout through the OEM codepage, not UTF-8, so aitm.cs's "•" and
    // "—" (neither representable in that codepage) arrive corrupted regardless of the encoding this
    // side decodes with — a capture artifact, not a behaviour difference (mcp.cs's in-process oracle
    // above needs no such workaround). Both sides get the same substitution before comparing shape.
    private static string Normalize(string s) => s.Replace("\r\n", "\n").Trim().Replace('•', '*').Replace('—', '-').Replace("\a", "*");

    // The oracle and the new tool run at slightly different times, so the "(N.NNms)" tail never
    // matches byte for byte; strip it the same way ImportToolTests strips the trailing db path.
    private static string StripTiming(string s) => System.Text.RegularExpressions.Regex.Replace(s, @"\(\d+[.,]\d+ms\)\s*$", "").TrimEnd();
}
