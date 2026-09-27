using System.Reflection;
using Grimora.Docs.Tools;
using Grimora.Store.Data;
using Grimora.TestSupport;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Grimora.Docs.Tests;

public class DocToolTests
{
    [Fact]
    public void CliShapeMatchesTodaysCliOutputForAConfidentMatch()
    {
        string instance = GrimoraCliRunner.NewTestInstance("doc-cli-hit");
        string dir = MakeFixtureDir("doc-cli-hit-fixture");
        try
        {
            GrimoraCliRunner.Run($"init --instance {instance}");
            GrimoraCliRunner.Run($"index-docs --instance {instance} --from \"{dir}\"");

            (string stdout, int exitCode) = GrimoraCliRunner.Run($"doc --instance {instance} docclihittopic");
            Assert.Equal(0, exitCode);
            string expected = Normalize(stdout);

            string dbPath = GrimoraCliRunner.InstanceDbPath(instance);
            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = Normalize(new DocTool().ExecuteCli(connection, "docclihittopic"));

            Assert.Equal(expected, actual);
            Assert.Contains("docclihittopic", actual);
        }
        finally
        {
            GrimoraCliRunner.DeleteInstance(instance);
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void CliShapeReportsNoDocsMatchForAGapQuery()
    {
        string instance = GrimoraCliRunner.NewTestInstance("doc-cli-gap");
        try
        {
            GrimoraCliRunner.Run($"init --instance {instance}");

            (string stdout, int exitCode) = GrimoraCliRunner.Run($"doc --instance {instance} nothing-ever-matches-this-doc-term");
            Assert.Equal(0, exitCode);
            string expected = Normalize(stdout);

            string dbPath = GrimoraCliRunner.InstanceDbPath(instance);
            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = Normalize(new DocTool().ExecuteCli(connection, "nothing-ever-matches-this-doc-term"));

            Assert.Equal(expected, actual);
            Assert.Contains("no docs match", actual);
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
        string instance = GrimoraCliRunner.NewTestInstance("doc-cli-hyphen");
        string dir = Path.Combine(Path.GetTempPath(), $"grimora-doc-cli-hyphen-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "notes.md"),
            "# doc-cli-hyphentopic\n\nThis section carries the durable knowledge worth absorbing here, long enough to pass the length gate.\n");
        try
        {
            GrimoraCliRunner.Run($"init --instance {instance}");
            GrimoraCliRunner.Run($"index-docs --instance {instance} --from \"{dir}\"");

            (string stdout, int exitCode) = GrimoraCliRunner.Run($"doc --instance {instance} \"doc-cli-hyphentopic\"");
            Assert.Equal(0, exitCode);
            string expected = Normalize(stdout);

            string dbPath = GrimoraCliRunner.InstanceDbPath(instance);
            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = Normalize(new DocTool().ExecuteCli(connection, "doc-cli-hyphentopic"));

            Assert.Equal(expected, actual);
            Assert.Contains("hyphentopic", actual);
        }
        finally
        {
            GrimoraCliRunner.DeleteInstance(instance);
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void McpShapeMatchesTodaysMcpOutputForAConfidentMatch()
    {
        string instance = GrimoraCliRunner.NewTestInstance("doc-mcp-hit");
        string dir = MakeFixtureDir("doc-mcp-hit-fixture");
        string? previousInstanceEnv = Environment.GetEnvironmentVariable("GRIMORA_INSTANCE");
        try
        {
            GrimoraCliRunner.Run($"init --instance {instance}");
            GrimoraCliRunner.Run($"index-docs --instance {instance} --from \"{dir}\"");

            Environment.SetEnvironmentVariable("GRIMORA_INSTANCE", instance);
            string expected = (string)InvokeMcpDoc("docmcphittopic")!;

            string dbPath = GrimoraCliRunner.InstanceDbPath(instance);
            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = new DocTool().ExecuteMcp(connection, "docmcphittopic");

            Assert.Equal(expected, actual);
            Assert.Contains("docmcphittopic", actual);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GRIMORA_INSTANCE", previousInstanceEnv);
            GrimoraCliRunner.DeleteInstance(instance);
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void McpShapeReportsGapSuffixWhenNoMatch()
    {
        string instance = GrimoraCliRunner.NewTestInstance("doc-mcp-gap");
        string? previousInstanceEnv = Environment.GetEnvironmentVariable("GRIMORA_INSTANCE");
        try
        {
            GrimoraCliRunner.Run($"init --instance {instance}");

            Environment.SetEnvironmentVariable("GRIMORA_INSTANCE", instance);
            string expected = (string)InvokeMcpDoc("nothing-ever-matches-this-mcp-doc-term")!;

            string dbPath = GrimoraCliRunner.InstanceDbPath(instance);
            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = new DocTool().ExecuteMcp(connection, "nothing-ever-matches-this-mcp-doc-term");

            Assert.Equal(expected, actual);
            Assert.Contains("gap logged", actual);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GRIMORA_INSTANCE", previousInstanceEnv);
            GrimoraCliRunner.DeleteInstance(instance);
        }
    }

    private static string MakeFixtureDir(string label)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"grimora-{label}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        string session = label.Replace("-fixture", "").Replace("-", "");
        File.WriteAllText(Path.Combine(dir, "notes.md"),
            $"# {session}topic\n\nThis section carries the durable knowledge worth absorbing here, long enough to pass the length gate.\n");
        return dir;
    }

    private static object? InvokeMcpDoc(string query)
    {
        Assembly mcp = Assembly.LoadFrom(FindMcpDll());
        Type tools = mcp.GetType("GrimoraTools") ?? throw new InvalidOperationException("GrimoraTools type not found in mcp.dll");
        MethodInfo method = tools.GetMethod("doc", BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException("GrimoraTools.doc not found in mcp.dll");
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
