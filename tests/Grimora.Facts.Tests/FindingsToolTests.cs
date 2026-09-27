using System.Reflection;
using Grimora.TestSupport;
using Grimora.Facts.Tools;
using Grimora.Store.Data;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Grimora.Facts.Tests;

public class FindingsToolTests
{
    [Fact]
    public void CliShapeMatchesTodaysCliOutputForOpenFindings()
    {
        string oldInstance = GrimoraCliRunner.NewTestInstance("findings-cli-old");
        string newInstance = GrimoraCliRunner.NewTestInstance("findings-cli-new");
        try
        {
            // Oracle: today's grimora.cs ListRows() (grimora.cs:592) called for the `findings` verb (grimora.cs:222).
            GrimoraCliRunner.Run($"init --instance {oldInstance}");
            GrimoraCliRunner.Run($"finding --instance {oldInstance} --title findings-fixture-one");
            (string stdout, int exitCode) = GrimoraCliRunner.Run($"findings --instance {oldInstance}");
            Assert.Equal(0, exitCode);
            string expected = Normalize(stdout);

            // New: FindingsTool.
            GrimoraCliRunner.Run($"init --instance {newInstance}");
            GrimoraCliRunner.Run($"finding --instance {newInstance} --title findings-fixture-one");
            string dbPath = GrimoraCliRunner.InstanceDbPath(newInstance);
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
            GrimoraCliRunner.DeleteInstance(oldInstance);
            GrimoraCliRunner.DeleteInstance(newInstance);
        }
    }

    // mcp.cs's `open_findings` resolves its own connection from Grimora_INSTANCE.
    [Fact]
    public void McpShapeMatchesTodaysMcpOutputForOpenFindings()
    {
        string instance = GrimoraCliRunner.NewTestInstance("findings-mcp");
        string? previousInstanceEnv = Environment.GetEnvironmentVariable("Grimora_INSTANCE");
        try
        {
            GrimoraCliRunner.Run($"init --instance {instance}");
            GrimoraCliRunner.Run($"finding --instance {instance} --title findings-fixture-mcp --source some-source");

            Environment.SetEnvironmentVariable("Grimora_INSTANCE", instance);
            string expected = (string)InvokeMcpOpenFindings()!;

            string dbPath = GrimoraCliRunner.InstanceDbPath(instance);
            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = new FindingsTool().ExecuteMcp(connection);

            Assert.Equal(expected, actual);
            Assert.Contains("findings-fixture-mcp", actual);
        }
        finally
        {
            Environment.SetEnvironmentVariable("Grimora_INSTANCE", previousInstanceEnv);
            GrimoraCliRunner.DeleteInstance(instance);
        }
    }

    [Fact]
    public void McpShapeReportsNoOpenFindingsOnAFreshStore()
    {
        string instance = GrimoraCliRunner.NewTestInstance("findings-mcp-empty");
        string? previousInstanceEnv = Environment.GetEnvironmentVariable("Grimora_INSTANCE");
        try
        {
            GrimoraCliRunner.Run($"init --instance {instance}");

            Environment.SetEnvironmentVariable("Grimora_INSTANCE", instance);
            string expected = (string)InvokeMcpOpenFindings()!;

            string dbPath = GrimoraCliRunner.InstanceDbPath(instance);
            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = new FindingsTool().ExecuteMcp(connection);

            Assert.Equal(expected, actual);
            Assert.Equal("no open findings.", actual);
        }
        finally
        {
            Environment.SetEnvironmentVariable("Grimora_INSTANCE", previousInstanceEnv);
            GrimoraCliRunner.DeleteInstance(instance);
        }
    }

    private static object? InvokeMcpOpenFindings()
    {
        Assembly mcp = Assembly.LoadFrom(FindMcpDll());
        Type tools = mcp.GetType("GrimoraTools") ?? throw new InvalidOperationException("GrimoraTools type not found in mcp.dll");
        MethodInfo method = tools.GetMethod("open_findings", BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException("GrimoraTools.open_findings not found in mcp.dll");
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
