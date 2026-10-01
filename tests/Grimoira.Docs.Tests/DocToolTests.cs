using System.Reflection;
using Grimoira.Docs.Tools;
using Grimoira.Store.Data;
using Grimoira.TestSupport;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Grimoira.Docs.Tests;

public class DocToolTests
{
    [Fact]
    public void CliShapeMatchesTodaysCliOutputForAConfidentMatch()
    {
        string instance = GrimoiraCliRunner.NewTestInstance("doc-cli-hit");
        string dir = MakeFixtureDir("doc-cli-hit-fixture");
        try
        {
            GrimoiraCliRunner.Run($"init --instance {instance}");
            GrimoiraCliRunner.Run($"index-docs --instance {instance} --from \"{dir}\"");

            (string stdout, int exitCode) = GrimoiraCliRunner.Run($"doc --instance {instance} docclihittopic");
            Assert.Equal(0, exitCode);
            string expected = Normalize(stdout);

            string dbPath = GrimoiraCliRunner.InstanceDbPath(instance);
            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = Normalize(new DocTool().ExecuteCli(connection, "docclihittopic"));

            Assert.Equal(expected, actual);
            Assert.Contains("docclihittopic", actual);
        }
        finally
        {
            GrimoiraCliRunner.DeleteInstance(instance);
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void CliShapeReportsNoDocsMatchForAGapQuery()
    {
        string instance = GrimoiraCliRunner.NewTestInstance("doc-cli-gap");
        try
        {
            GrimoiraCliRunner.Run($"init --instance {instance}");

            (string stdout, int exitCode) = GrimoiraCliRunner.Run($"doc --instance {instance} nothing-ever-matches-this-doc-term");
            Assert.Equal(0, exitCode);
            string expected = Normalize(stdout);

            string dbPath = GrimoiraCliRunner.InstanceDbPath(instance);
            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = Normalize(new DocTool().ExecuteCli(connection, "nothing-ever-matches-this-doc-term"));

            Assert.Equal(expected, actual);
            Assert.Contains("no docs match", actual);
        }
        finally
        {
            GrimoiraCliRunner.DeleteInstance(instance);
        }
    }

    // CliTokens (the shared Tokens() used by ExecuteCli) splits on hyphens the same way grimoira.cs's own
    // Tokens does, unlike the MCP path's tokenizer — pins that the CLI shape stays untouched by the
    // MCP-side tokenizer fix.
    [Fact]
    public void CliShapeMatchesTodaysCliOutputForAHyphenatedQuery()
    {
        string instance = GrimoiraCliRunner.NewTestInstance("doc-cli-hyphen");
        string dir = Path.Combine(Path.GetTempPath(), $"grimoira-doc-cli-hyphen-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "notes.md"),
            "# doc-cli-hyphentopic\n\nThis section carries the durable knowledge worth absorbing here, long enough to pass the length gate.\n");
        try
        {
            GrimoiraCliRunner.Run($"init --instance {instance}");
            GrimoiraCliRunner.Run($"index-docs --instance {instance} --from \"{dir}\"");

            (string stdout, int exitCode) = GrimoiraCliRunner.Run($"doc --instance {instance} \"doc-cli-hyphentopic\"");
            Assert.Equal(0, exitCode);
            string expected = Normalize(stdout);

            string dbPath = GrimoiraCliRunner.InstanceDbPath(instance);
            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = Normalize(new DocTool().ExecuteCli(connection, "doc-cli-hyphentopic"));

            Assert.Equal(expected, actual);
            Assert.Contains("hyphentopic", actual);
        }
        finally
        {
            GrimoiraCliRunner.DeleteInstance(instance);
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void McpShapeMatchesTodaysMcpOutputForAConfidentMatch()
    {
        string instance = GrimoiraCliRunner.NewTestInstance("doc-mcp-hit");
        string dir = MakeFixtureDir("doc-mcp-hit-fixture");
        string? previousInstanceEnv = Environment.GetEnvironmentVariable("GRIMOIRA_INSTANCE");
        try
        {
            GrimoiraCliRunner.Run($"init --instance {instance}");
            GrimoiraCliRunner.Run($"index-docs --instance {instance} --from \"{dir}\"");

            Environment.SetEnvironmentVariable("GRIMOIRA_INSTANCE", instance);
            string expected = (string)InvokeMcpDoc("docmcphittopic")!;

            string dbPath = GrimoiraCliRunner.InstanceDbPath(instance);
            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = new DocTool().ExecuteMcp(connection, "docmcphittopic");

            Assert.Equal(expected, actual);
            Assert.Contains("docmcphittopic", actual);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GRIMOIRA_INSTANCE", previousInstanceEnv);
            GrimoiraCliRunner.DeleteInstance(instance);
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void McpShapeReportsGapSuffixWhenNoMatch()
    {
        string instance = GrimoiraCliRunner.NewTestInstance("doc-mcp-gap");
        string? previousInstanceEnv = Environment.GetEnvironmentVariable("GRIMOIRA_INSTANCE");
        try
        {
            GrimoiraCliRunner.Run($"init --instance {instance}");

            Environment.SetEnvironmentVariable("GRIMOIRA_INSTANCE", instance);
            string expected = (string)InvokeMcpDoc("nothing-ever-matches-this-mcp-doc-term")!;

            string dbPath = GrimoiraCliRunner.InstanceDbPath(instance);
            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = new DocTool().ExecuteMcp(connection, "nothing-ever-matches-this-mcp-doc-term");

            Assert.Equal(expected, actual);
            Assert.Contains("gap logged", actual);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GRIMOIRA_INSTANCE", previousInstanceEnv);
            GrimoiraCliRunner.DeleteInstance(instance);
        }
    }

    private static string MakeFixtureDir(string label)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"grimoira-{label}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        string session = label.Replace("-fixture", "").Replace("-", "");
        File.WriteAllText(Path.Combine(dir, "notes.md"),
            $"# {session}topic\n\nThis section carries the durable knowledge worth absorbing here, long enough to pass the length gate.\n");
        return dir;
    }

    private static object? InvokeMcpDoc(string query)
    {
        Assembly mcp = Assembly.LoadFrom(FindMcpDll());
        Type tools = mcp.GetType("GrimoiraTools") ?? throw new InvalidOperationException("GrimoiraTools type not found in mcp.dll");
        MethodInfo method = tools.GetMethod("doc", BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException("GrimoiraTools.doc not found in mcp.dll");
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
