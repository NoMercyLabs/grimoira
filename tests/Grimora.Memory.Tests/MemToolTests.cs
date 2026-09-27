using System.Reflection;
using Grimora.Memory.Tools;
using Grimora.Store.Data;
using Grimora.TestSupport;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Grimora.Memory.Tests;

public class MemToolTests
{
    [Fact]
    public void CliShapeMatchesTodaysCliOutputForAConfidentMatch()
    {
        string instance = GrimoraCliRunner.NewTestInstance("mem-cli-hit");
        string memDir = MakeMemoryDir("mem-cli-hit", ("mem-fixture", "feedback", "Mem Fixture", "a memory about the mem fixture topic"));
        try
        {
            GrimoraCliRunner.Run($"init --instance {instance}");
            GrimoraCliRunner.Run($"index-memory --instance {instance} --from \"{memDir}\"");

            (string stdout, int exitCode) = GrimoraCliRunner.Run($"mem --instance {instance} mem-fixture");
            Assert.Equal(0, exitCode);
            string expected = Normalize(stdout);

            string dbPath = GrimoraCliRunner.InstanceDbPath(instance);
            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = Normalize(new MemTool(new UsageSignal()).ExecuteCli(connection, "mem-fixture", hard: false));

            Assert.Equal(expected, actual);
            Assert.Contains("mem fixture topic", actual);
        }
        finally
        {
            GrimoraCliRunner.DeleteInstance(instance);
            Directory.Delete(memDir, recursive: true);
        }
    }

    [Fact]
    public void CliShapeReportsNoMemoryMatchesForAGapQuery()
    {
        string instance = GrimoraCliRunner.NewTestInstance("mem-cli-gap");
        try
        {
            GrimoraCliRunner.Run($"init --instance {instance}");

            (string stdout, int exitCode) = GrimoraCliRunner.Run($"mem --instance {instance} nothing-ever-matches-this-term");
            Assert.Equal(0, exitCode);
            string expected = Normalize(stdout);

            string dbPath = GrimoraCliRunner.InstanceDbPath(instance);
            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = Normalize(new MemTool(new UsageSignal()).ExecuteCli(connection, "nothing-ever-matches-this-term", hard: false));

            Assert.Equal(expected, actual);
            Assert.Contains("gap logged", actual);
        }
        finally
        {
            GrimoraCliRunner.DeleteInstance(instance);
        }
    }

    // CliTokens (the shared Tokens() used by ExecuteCli) splits on hyphens the same way grimora.cs's own
    // Tokens does, unlike the MCP path's tokenizer — pins that the CLI shape stays untouched by the
    // MCP-side tokenizer fix.
    [Fact]
    public void CliShapeMatchesTodaysCliOutputForAHyphenatedQuery()
    {
        string instance = GrimoraCliRunner.NewTestInstance("mem-cli-hyphen");
        string memDir = MakeMemoryDir("mem-cli-hyphen", ("mem-hyphen-fixture", "feedback", "Mem Hyphen Fixture", "a memory about the mem-hyphen-topic subject"));
        try
        {
            GrimoraCliRunner.Run($"init --instance {instance}");
            GrimoraCliRunner.Run($"index-memory --instance {instance} --from \"{memDir}\"");

            (string stdout, int exitCode) = GrimoraCliRunner.Run($"mem --instance {instance} mem-hyphen-topic");
            Assert.Equal(0, exitCode);
            string expected = Normalize(stdout);

            string dbPath = GrimoraCliRunner.InstanceDbPath(instance);
            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = Normalize(new MemTool(new UsageSignal()).ExecuteCli(connection, "mem-hyphen-topic", hard: false));

            Assert.Equal(expected, actual);
            Assert.Contains("mem-hyphen-topic", actual);
        }
        finally
        {
            GrimoraCliRunner.DeleteInstance(instance);
            Directory.Delete(memDir, recursive: true);
        }
    }

    [Fact]
    public void CliShapeListsOnlyHardRulesWhenHardIsSet()
    {
        string instance = GrimoraCliRunner.NewTestInstance("mem-cli-hard");
        string memDir = MakeMemoryDir("mem-cli-hard", ("mem-hard-fixture", "feedback", "Mem Hard Rule Fixture", "always do this"));
        try
        {
            GrimoraCliRunner.Run($"init --instance {instance}");
            GrimoraCliRunner.Run($"index-memory --instance {instance} --from \"{memDir}\"");

            (string stdout, int exitCode) = GrimoraCliRunner.Run($"mem --instance {instance} --hard");
            Assert.Equal(0, exitCode);
            string expected = Normalize(stdout);

            string dbPath = GrimoraCliRunner.InstanceDbPath(instance);
            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = Normalize(new MemTool(new UsageSignal()).ExecuteCli(connection, "", hard: true));

            Assert.Equal(expected, actual);
            Assert.Contains("Mem Hard Rule Fixture", actual);
        }
        finally
        {
            GrimoraCliRunner.DeleteInstance(instance);
            Directory.Delete(memDir, recursive: true);
        }
    }

    [Fact]
    public void McpShapeMatchesTodaysMcpOutputForAConfidentMatch()
    {
        string instance = GrimoraCliRunner.NewTestInstance("mem-mcp-hit");
        string memDir = MakeMemoryDir("mem-mcp-hit", ("memmcpfixture", "feedback", "Mem Mcp Fixture", "a memory about the memmcpfixtureword subject"));
        string? previousInstanceEnv = Environment.GetEnvironmentVariable("GRIMORA_INSTANCE");
        try
        {
            GrimoraCliRunner.Run($"init --instance {instance}");
            GrimoraCliRunner.Run($"index-memory --instance {instance} --from \"{memDir}\"");

            Environment.SetEnvironmentVariable("GRIMORA_INSTANCE", instance);
            string expected = (string)InvokeMcpRule("memmcpfixtureword")!;

            string dbPath = GrimoraCliRunner.InstanceDbPath(instance);
            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = new MemTool(new UsageSignal()).ExecuteMcp(connection, "memmcpfixtureword");

            Assert.Equal(expected, actual);
            Assert.Contains("memmcpfixtureword", actual);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GRIMORA_INSTANCE", previousInstanceEnv);
            GrimoraCliRunner.DeleteInstance(instance);
            Directory.Delete(memDir, recursive: true);
        }
    }

    [Fact]
    public void McpShapeReportsGapSuffixWhenNoMatch()
    {
        string instance = GrimoraCliRunner.NewTestInstance("mem-mcp-gap");
        string? previousInstanceEnv = Environment.GetEnvironmentVariable("GRIMORA_INSTANCE");
        try
        {
            GrimoraCliRunner.Run($"init --instance {instance}");

            Environment.SetEnvironmentVariable("GRIMORA_INSTANCE", instance);
            string expected = (string)InvokeMcpRule("nothing-ever-matches-this-mcp-term")!;

            string dbPath = GrimoraCliRunner.InstanceDbPath(instance);
            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = new MemTool(new UsageSignal()).ExecuteMcp(connection, "nothing-ever-matches-this-mcp-term");

            Assert.Equal(expected, actual);
            Assert.Contains("gap logged", actual);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GRIMORA_INSTANCE", previousInstanceEnv);
            GrimoraCliRunner.DeleteInstance(instance);
        }
    }

    private static string MakeMemoryDir(string label, params (string slug, string type, string name, string description)[] entries)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"grimora-mem-fixture-{label}-{Guid.NewGuid():N}");
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
        Type tools = mcp.GetType("GrimoraTools") ?? throw new InvalidOperationException("GrimoraTools type not found in mcp.dll");
        MethodInfo method = tools.GetMethod("rule", BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException("GrimoraTools.rule not found in mcp.dll");
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
