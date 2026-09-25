using System.Reflection;
using Aitm.Docs.Tools;
using Aitm.Store.Data;
using Aitm.TestSupport;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Aitm.Docs.Tests;

public class DocToolTests
{
    [Fact]
    public void CliShapeMatchesTodaysCliOutputForAConfidentMatch()
    {
        string instance = AitmCliRunner.NewTestInstance("doc-cli-hit");
        string dir = MakeFixtureDir("doc-cli-hit-fixture");
        try
        {
            AitmCliRunner.Run($"init --instance {instance}");
            AitmCliRunner.Run($"index-docs --instance {instance} --from \"{dir}\"");

            (string stdout, int exitCode) = AitmCliRunner.Run($"doc --instance {instance} docclihittopic");
            Assert.Equal(0, exitCode);
            string expected = Normalize(stdout);

            string dbPath = AitmCliRunner.InstanceDbPath(instance);
            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = Normalize(new DocTool().ExecuteCli(connection, "docclihittopic"));

            Assert.Equal(expected, actual);
            Assert.Contains("docclihittopic", actual);
        }
        finally
        {
            AitmCliRunner.DeleteInstance(instance);
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void CliShapeReportsNoDocsMatchForAGapQuery()
    {
        string instance = AitmCliRunner.NewTestInstance("doc-cli-gap");
        try
        {
            AitmCliRunner.Run($"init --instance {instance}");

            (string stdout, int exitCode) = AitmCliRunner.Run($"doc --instance {instance} nothing-ever-matches-this-doc-term");
            Assert.Equal(0, exitCode);
            string expected = Normalize(stdout);

            string dbPath = AitmCliRunner.InstanceDbPath(instance);
            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = Normalize(new DocTool().ExecuteCli(connection, "nothing-ever-matches-this-doc-term"));

            Assert.Equal(expected, actual);
            Assert.Contains("no docs match", actual);
        }
        finally
        {
            AitmCliRunner.DeleteInstance(instance);
        }
    }

    [Fact]
    public void McpShapeMatchesTodaysMcpOutputForAConfidentMatch()
    {
        string instance = AitmCliRunner.NewTestInstance("doc-mcp-hit");
        string dir = MakeFixtureDir("doc-mcp-hit-fixture");
        string? previousInstanceEnv = Environment.GetEnvironmentVariable("AITM_INSTANCE");
        try
        {
            AitmCliRunner.Run($"init --instance {instance}");
            AitmCliRunner.Run($"index-docs --instance {instance} --from \"{dir}\"");

            Environment.SetEnvironmentVariable("AITM_INSTANCE", instance);
            string expected = (string)InvokeMcpDoc("docmcphittopic")!;

            string dbPath = AitmCliRunner.InstanceDbPath(instance);
            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = new DocTool().ExecuteMcp(connection, "docmcphittopic");

            Assert.Equal(expected, actual);
            Assert.Contains("docmcphittopic", actual);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AITM_INSTANCE", previousInstanceEnv);
            AitmCliRunner.DeleteInstance(instance);
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void McpShapeReportsGapSuffixWhenNoMatch()
    {
        string instance = AitmCliRunner.NewTestInstance("doc-mcp-gap");
        string? previousInstanceEnv = Environment.GetEnvironmentVariable("AITM_INSTANCE");
        try
        {
            AitmCliRunner.Run($"init --instance {instance}");

            Environment.SetEnvironmentVariable("AITM_INSTANCE", instance);
            string expected = (string)InvokeMcpDoc("nothing-ever-matches-this-mcp-doc-term")!;

            string dbPath = AitmCliRunner.InstanceDbPath(instance);
            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = new DocTool().ExecuteMcp(connection, "nothing-ever-matches-this-mcp-doc-term");

            Assert.Equal(expected, actual);
            Assert.Contains("gap logged", actual);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AITM_INSTANCE", previousInstanceEnv);
            AitmCliRunner.DeleteInstance(instance);
        }
    }

    private static string MakeFixtureDir(string label)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"aitm-{label}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        string session = label.Replace("-fixture", "").Replace("-", "");
        File.WriteAllText(Path.Combine(dir, "notes.md"),
            $"# {session}topic\n\nThis section carries the durable knowledge worth absorbing here, long enough to pass the length gate.\n");
        return dir;
    }

    private static object? InvokeMcpDoc(string query)
    {
        Assembly mcp = Assembly.LoadFrom(FindMcpDll());
        Type tools = mcp.GetType("AitmTools") ?? throw new InvalidOperationException("AitmTools type not found in mcp.dll");
        MethodInfo method = tools.GetMethod("doc", BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException("AitmTools.doc not found in mcp.dll");
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
