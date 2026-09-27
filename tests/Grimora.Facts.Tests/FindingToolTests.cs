using System.Reflection;
using Grimora.TestSupport;
using Grimora.Facts.Tools;
using Grimora.Store.Data;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Grimora.Facts.Tests;

public class FindingToolTests
{
    [Fact]
    public void CliShapeMatchesTodaysCliOutputAndLogsTheMutation()
    {
        string oldInstance = GrimoraCliRunner.NewTestInstance("finding-cli-old");
        string newInstance = GrimoraCliRunner.NewTestInstance("finding-cli-new");
        try
        {
            // Oracle: today's grimora.cs AddFinding() (grimora.cs:673).
            GrimoraCliRunner.Run($"init --instance {oldInstance}");
            (string stdout, int exitCode) = GrimoraCliRunner.Run(
                $"finding --instance {oldInstance} --title finding-fixture --detail some-detail --source some-source");
            Assert.Equal(0, exitCode);
            string expected = stdout.Trim();

            // New: FindingTool.
            GrimoraCliRunner.Run($"init --instance {newInstance}");
            string dbPath = GrimoraCliRunner.InstanceDbPath(newInstance);
            string actual;
            using (SqliteConnection connection = StoreConnection.Open(dbPath))
            {
                actual = new FindingTool().ExecuteCli(connection, "finding-fixture", "some-detail", "some-source").Trim();
            }

            Assert.Equal(expected, actual);
            Assert.Equal("finding logged.", actual);

            using SqliteConnection check = new($"Data Source={dbPath};Mode=ReadOnly");
            check.Open();
            using SqliteCommand select = check.CreateCommand();
            select.CommandText = "SELECT title, detail, source, status FROM findings";
            using SqliteDataReader reader = select.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal("finding-fixture", reader.GetString(0));
            Assert.Equal("some-detail", reader.GetString(1));
            Assert.Equal("some-source", reader.GetString(2));
            Assert.Equal("open", reader.GetString(3));

            using SqliteCommand mutations = check.CreateCommand();
            mutations.CommandText = "SELECT count(*) FROM mutations WHERE kind='finding' AND k='finding-fixture'";
            Assert.Equal(1L, (long)(mutations.ExecuteScalar() ?? 0L));
        }
        finally
        {
            GrimoraCliRunner.DeleteInstance(oldInstance);
            GrimoraCliRunner.DeleteInstance(newInstance);
        }
    }

    // mcp.cs's `log_finding` resolves its own connection from GRIMORA_INSTANCE.
    [Fact]
    public void McpShapeMatchesTodaysMcpOutputAndDoesNotLogAMutation()
    {
        string instance = GrimoraCliRunner.NewTestInstance("finding-mcp");
        string? previousInstanceEnv = Environment.GetEnvironmentVariable("GRIMORA_INSTANCE");
        try
        {
            GrimoraCliRunner.Run($"init --instance {instance}");

            Environment.SetEnvironmentVariable("GRIMORA_INSTANCE", instance);
            string expected = (string)InvokeMcpLogFinding("finding-fixture-mcp", "detail-mcp", "source-mcp")!;

            string dbPath = GrimoraCliRunner.InstanceDbPath(instance);
            string actual;
            using (SqliteConnection connection = StoreConnection.Open(dbPath))
            {
                actual = new FindingTool().ExecuteMcp(connection, "finding-fixture-mcp-2", "detail-mcp", "source-mcp");
            }

            // Oracle wrote its own row (`finding-fixture-mcp`); the new tool writes a second, distinct
            // row (`finding-fixture-mcp-2`) on the same store, so both messages are compared by shape.
            Assert.Equal("finding logged: finding-fixture-mcp", expected);
            Assert.Equal("finding logged: finding-fixture-mcp-2", actual);

            using SqliteConnection check = new($"Data Source={dbPath};Mode=ReadOnly");
            check.Open();
            using SqliteCommand mutations = check.CreateCommand();
            mutations.CommandText = "SELECT count(*) FROM mutations WHERE kind='finding'";
            Assert.Equal(0L, (long)(mutations.ExecuteScalar() ?? 0L));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GRIMORA_INSTANCE", previousInstanceEnv);
            GrimoraCliRunner.DeleteInstance(instance);
        }
    }

    private static object? InvokeMcpLogFinding(string title, string detail, string source)
    {
        Assembly mcp = Assembly.LoadFrom(FindMcpDll());
        Type tools = mcp.GetType("GrimoraTools") ?? throw new InvalidOperationException("GrimoraTools type not found in mcp.dll");
        MethodInfo method = tools.GetMethod("log_finding", BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException("GrimoraTools.log_finding not found in mcp.dll");
        return method.Invoke(null, [title, detail, source]);
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
}
