using System.Reflection;
using Aitm.TestSupport;
using Aitm.Facts.Tools;
using Aitm.Store.Data;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Aitm.Facts.Tests;

public class FindingsToolTests
{
    [Fact]
    public void CliShapeMatchesTodaysCliOutputForOpenFindings()
    {
        string oldInstance = AitmCliRunner.NewTestInstance("findings-cli-old");
        string newInstance = AitmCliRunner.NewTestInstance("findings-cli-new");
        try
        {
            // Oracle: today's aitm.cs ListRows() (aitm.cs:592) called for the `findings` verb (aitm.cs:222).
            AitmCliRunner.Run($"init --instance {oldInstance}");
            AitmCliRunner.Run($"finding --instance {oldInstance} --title findings-fixture-one");
            (string stdout, int exitCode) = AitmCliRunner.Run($"findings --instance {oldInstance}");
            Assert.Equal(0, exitCode);
            string expected = Normalize(stdout);

            // New: FindingsTool.
            AitmCliRunner.Run($"init --instance {newInstance}");
            AitmCliRunner.Run($"finding --instance {newInstance} --title findings-fixture-one");
            string dbPath = AitmCliRunner.InstanceDbPath(newInstance);
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
            AitmCliRunner.DeleteInstance(oldInstance);
            AitmCliRunner.DeleteInstance(newInstance);
        }
    }

    // mcp.cs's `open_findings` resolves its own connection from AITM_INSTANCE.
    [Fact]
    public void McpShapeMatchesTodaysMcpOutputForOpenFindings()
    {
        string instance = AitmCliRunner.NewTestInstance("findings-mcp");
        string? previousInstanceEnv = Environment.GetEnvironmentVariable("AITM_INSTANCE");
        try
        {
            AitmCliRunner.Run($"init --instance {instance}");
            AitmCliRunner.Run($"finding --instance {instance} --title findings-fixture-mcp --source some-source");

            Environment.SetEnvironmentVariable("AITM_INSTANCE", instance);
            string expected = (string)InvokeMcpOpenFindings()!;

            string dbPath = AitmCliRunner.InstanceDbPath(instance);
            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = new FindingsTool().ExecuteMcp(connection);

            Assert.Equal(expected, actual);
            Assert.Contains("findings-fixture-mcp", actual);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AITM_INSTANCE", previousInstanceEnv);
            AitmCliRunner.DeleteInstance(instance);
        }
    }

    [Fact]
    public void McpShapeReportsNoOpenFindingsOnAFreshStore()
    {
        string instance = AitmCliRunner.NewTestInstance("findings-mcp-empty");
        string? previousInstanceEnv = Environment.GetEnvironmentVariable("AITM_INSTANCE");
        try
        {
            AitmCliRunner.Run($"init --instance {instance}");

            Environment.SetEnvironmentVariable("AITM_INSTANCE", instance);
            string expected = (string)InvokeMcpOpenFindings()!;

            string dbPath = AitmCliRunner.InstanceDbPath(instance);
            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = new FindingsTool().ExecuteMcp(connection);

            Assert.Equal(expected, actual);
            Assert.Equal("no open findings.", actual);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AITM_INSTANCE", previousInstanceEnv);
            AitmCliRunner.DeleteInstance(instance);
        }
    }

    private static object? InvokeMcpOpenFindings()
    {
        Assembly mcp = Assembly.LoadFrom(FindMcpDll());
        Type tools = mcp.GetType("AitmTools") ?? throw new InvalidOperationException("AitmTools type not found in mcp.dll");
        MethodInfo method = tools.GetMethod("open_findings", BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException("AitmTools.open_findings not found in mcp.dll");
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
