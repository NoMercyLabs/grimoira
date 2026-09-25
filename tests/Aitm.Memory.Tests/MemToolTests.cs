using System.Reflection;
using Aitm.Memory.Tools;
using Aitm.Store.Data;
using Aitm.TestSupport;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Aitm.Memory.Tests;

public class MemToolTests
{
    [Fact]
    public void CliShapeMatchesTodaysCliOutputForAConfidentMatch()
    {
        string instance = AitmCliRunner.NewTestInstance("mem-cli-hit");
        string memDir = MakeMemoryDir("mem-cli-hit", ("mem-fixture", "feedback", "Mem Fixture", "a memory about the mem fixture topic"));
        try
        {
            AitmCliRunner.Run($"init --instance {instance}");
            AitmCliRunner.Run($"index-memory --instance {instance} --from \"{memDir}\"");

            (string stdout, int exitCode) = AitmCliRunner.Run($"mem --instance {instance} mem-fixture");
            Assert.Equal(0, exitCode);
            string expected = Normalize(stdout);

            string dbPath = AitmCliRunner.InstanceDbPath(instance);
            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = Normalize(new MemTool(new UsageSignal()).ExecuteCli(connection, "mem-fixture", hard: false));

            Assert.Equal(expected, actual);
            Assert.Contains("mem fixture topic", actual);
        }
        finally
        {
            AitmCliRunner.DeleteInstance(instance);
            Directory.Delete(memDir, recursive: true);
        }
    }

    [Fact]
    public void CliShapeReportsNoMemoryMatchesForAGapQuery()
    {
        string instance = AitmCliRunner.NewTestInstance("mem-cli-gap");
        try
        {
            AitmCliRunner.Run($"init --instance {instance}");

            (string stdout, int exitCode) = AitmCliRunner.Run($"mem --instance {instance} nothing-ever-matches-this-term");
            Assert.Equal(0, exitCode);
            string expected = Normalize(stdout);

            string dbPath = AitmCliRunner.InstanceDbPath(instance);
            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = Normalize(new MemTool(new UsageSignal()).ExecuteCli(connection, "nothing-ever-matches-this-term", hard: false));

            Assert.Equal(expected, actual);
            Assert.Contains("gap logged", actual);
        }
        finally
        {
            AitmCliRunner.DeleteInstance(instance);
        }
    }

    [Fact]
    public void CliShapeListsOnlyHardRulesWhenHardIsSet()
    {
        string instance = AitmCliRunner.NewTestInstance("mem-cli-hard");
        string memDir = MakeMemoryDir("mem-cli-hard", ("mem-hard-fixture", "feedback", "Mem Hard Rule Fixture", "always do this"));
        try
        {
            AitmCliRunner.Run($"init --instance {instance}");
            AitmCliRunner.Run($"index-memory --instance {instance} --from \"{memDir}\"");

            (string stdout, int exitCode) = AitmCliRunner.Run($"mem --instance {instance} --hard");
            Assert.Equal(0, exitCode);
            string expected = Normalize(stdout);

            string dbPath = AitmCliRunner.InstanceDbPath(instance);
            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = Normalize(new MemTool(new UsageSignal()).ExecuteCli(connection, "", hard: true));

            Assert.Equal(expected, actual);
            Assert.Contains("Mem Hard Rule Fixture", actual);
        }
        finally
        {
            AitmCliRunner.DeleteInstance(instance);
            Directory.Delete(memDir, recursive: true);
        }
    }

    [Fact]
    public void McpShapeMatchesTodaysMcpOutputForAConfidentMatch()
    {
        string instance = AitmCliRunner.NewTestInstance("mem-mcp-hit");
        string memDir = MakeMemoryDir("mem-mcp-hit", ("memmcpfixture", "feedback", "Mem Mcp Fixture", "a memory about the memmcpfixtureword subject"));
        string? previousInstanceEnv = Environment.GetEnvironmentVariable("AITM_INSTANCE");
        try
        {
            AitmCliRunner.Run($"init --instance {instance}");
            AitmCliRunner.Run($"index-memory --instance {instance} --from \"{memDir}\"");

            Environment.SetEnvironmentVariable("AITM_INSTANCE", instance);
            string expected = (string)InvokeMcpRule("memmcpfixtureword")!;

            string dbPath = AitmCliRunner.InstanceDbPath(instance);
            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = new MemTool(new UsageSignal()).ExecuteMcp(connection, "memmcpfixtureword");

            Assert.Equal(expected, actual);
            Assert.Contains("memmcpfixtureword", actual);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AITM_INSTANCE", previousInstanceEnv);
            AitmCliRunner.DeleteInstance(instance);
            Directory.Delete(memDir, recursive: true);
        }
    }

    [Fact]
    public void McpShapeReportsGapSuffixWhenNoMatch()
    {
        string instance = AitmCliRunner.NewTestInstance("mem-mcp-gap");
        string? previousInstanceEnv = Environment.GetEnvironmentVariable("AITM_INSTANCE");
        try
        {
            AitmCliRunner.Run($"init --instance {instance}");

            Environment.SetEnvironmentVariable("AITM_INSTANCE", instance);
            string expected = (string)InvokeMcpRule("nothing-ever-matches-this-mcp-term")!;

            string dbPath = AitmCliRunner.InstanceDbPath(instance);
            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = new MemTool(new UsageSignal()).ExecuteMcp(connection, "nothing-ever-matches-this-mcp-term");

            Assert.Equal(expected, actual);
            Assert.Contains("gap logged", actual);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AITM_INSTANCE", previousInstanceEnv);
            AitmCliRunner.DeleteInstance(instance);
        }
    }

    private static string MakeMemoryDir(string label, params (string slug, string type, string name, string description)[] entries)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"aitm-mem-fixture-{label}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        foreach ((string slug, string type, string name, string description) in entries)
        {
            File.WriteAllText(Path.Combine(dir, slug + ".md"),
                $"---\ntype: {type}\nname: {name}\ndescription: {description}\n---\n\n{description}.\n");
        }
        return dir;
    }

    private static object? InvokeMcpRule(string query)
    {
        Assembly mcp = Assembly.LoadFrom(FindMcpDll());
        Type tools = mcp.GetType("AitmTools") ?? throw new InvalidOperationException("AitmTools type not found in mcp.dll");
        MethodInfo method = tools.GetMethod("rule", BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException("AitmTools.rule not found in mcp.dll");
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

    private static string Normalize(string s) => s.Replace("\r\n", "\n").Trim().Replace('•', '*').Replace('—', '-').Replace("\a", "*");
}
