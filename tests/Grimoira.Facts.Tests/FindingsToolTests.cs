using System.Reflection;
using Grimoira.TestSupport;
using Grimoira.Facts.Tools;
using Grimoira.Store.Data;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Grimoira.Facts.Tests;

public class FindingsToolTests
{
    [Fact]
    public void CliShapeMatchesTodaysCliOutputForOpenFindings()
    {
        string oldInstance = GrimoiraCliRunner.NewTestInstance("findings-cli-old");
        string newInstance = GrimoiraCliRunner.NewTestInstance("findings-cli-new");
        try
        {
            // Oracle: today's grimoira.cs ListRows() (grimoira.cs:592) called for the `findings` verb (grimoira.cs:222).
            GrimoiraCliRunner.Run($"init --instance {oldInstance}");
            GrimoiraCliRunner.Run($"finding --instance {oldInstance} --title findings-fixture-one");
            (string stdout, int exitCode) = GrimoiraCliRunner.Run($"findings --instance {oldInstance}");
            Assert.Equal(0, exitCode);
            string expected = Normalize(stdout);

            // New: FindingsTool.
            GrimoiraCliRunner.Run($"init --instance {newInstance}");
            GrimoiraCliRunner.Run($"finding --instance {newInstance} --title findings-fixture-one");
            string dbPath = GrimoiraCliRunner.InstanceDbPath(newInstance);
            string actual;
            using (SqliteConnection connection = StoreConnection.Open(dbPath))
            {
                actual = Normalize(new FindingsTool().ExecuteCli(connection));
            }

            Assert.Equal(expected, actual);
            Assert.Contains("(1 open finding(s))", actual);
        }
        finally
        {
            GrimoiraCliRunner.DeleteInstance(oldInstance);
            GrimoiraCliRunner.DeleteInstance(newInstance);
        }
    }

    // mcp.cs's `open_findings` resolves its own connection from GRIMOIRA_INSTANCE.
    [Fact]
    public void McpShapeMatchesTodaysMcpOutputForOpenFindings()
    {
        string instance = GrimoiraCliRunner.NewTestInstance("findings-mcp");
        string? previousInstanceEnv = Environment.GetEnvironmentVariable("GRIMOIRA_INSTANCE");
        try
        {
            GrimoiraCliRunner.Run($"init --instance {instance}");
            GrimoiraCliRunner.Run($"finding --instance {instance} --title findings-fixture-mcp --source some-source");

            Environment.SetEnvironmentVariable("GRIMOIRA_INSTANCE", instance);
            string expected = CliGoldens.Frozen("open_findings", () => (string)InvokeMcpOpenFindings()!);

            string dbPath = GrimoiraCliRunner.InstanceDbPath(instance);
            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = new FindingsTool().ExecuteMcp(connection);

            Assert.Equal(expected, actual);
            Assert.Contains("findings-fixture-mcp", actual);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GRIMOIRA_INSTANCE", previousInstanceEnv);
            GrimoiraCliRunner.DeleteInstance(instance);
        }
    }

    [Fact]
    public void McpShapeReportsNoOpenFindingsOnAFreshStore()
    {
        string instance = GrimoiraCliRunner.NewTestInstance("findings-mcp-empty");
        string? previousInstanceEnv = Environment.GetEnvironmentVariable("GRIMOIRA_INSTANCE");
        try
        {
            GrimoiraCliRunner.Run($"init --instance {instance}");

            Environment.SetEnvironmentVariable("GRIMOIRA_INSTANCE", instance);
            string expected = CliGoldens.Frozen("open_findings", () => (string)InvokeMcpOpenFindings()!);

            string dbPath = GrimoiraCliRunner.InstanceDbPath(instance);
            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = new FindingsTool().ExecuteMcp(connection);

            Assert.Equal(expected, actual);
            Assert.Equal("no open findings.", actual);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GRIMOIRA_INSTANCE", previousInstanceEnv);
            GrimoiraCliRunner.DeleteInstance(instance);
        }
    }

    private static object? InvokeMcpOpenFindings()
    {
        Assembly mcp = Assembly.LoadFrom(FindMcpDll());
        Type tools = mcp.GetType("GrimoiraTools") ?? throw new InvalidOperationException("GrimoiraTools type not found in mcp.dll");
        MethodInfo method = tools.GetMethod("open_findings", BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException("GrimoiraTools.open_findings not found in mcp.dll");
        return method.Invoke(null, []);
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
