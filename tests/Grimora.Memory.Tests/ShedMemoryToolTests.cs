using System.Reflection;
using Grimora.Memory.Tools;
using Grimora.Store.Data;
using Grimora.TestSupport;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Grimora.Memory.Tests;

public class ShedMemoryToolTests
{
    [Fact]
    public void CliShapeMatchesTodaysCliOutputAndDeletesTheRow()
    {
        string oldInstance = GrimoraCliRunner.NewTestInstance("shed-memory-old");
        string newInstance = GrimoraCliRunner.NewTestInstance("shed-memory-new");
        string memDir = MakeMemoryDir();
        try
        {
            // Oracle: today's grimora.cs ShedMemory() (grimora.cs:1175).
            GrimoraCliRunner.Run($"init --instance {oldInstance}");
            GrimoraCliRunner.Run($"index-memory --instance {oldInstance} --from \"{memDir}\"");
            (string stdout, int exitCode) = GrimoraCliRunner.Run($"shed-memory --instance {oldInstance} --key shed-memory-fixture");
            Assert.Equal(0, exitCode);
            string expected = stdout.Trim();

            // New: ShedMemoryTool.
            GrimoraCliRunner.Run($"init --instance {newInstance}");
            GrimoraCliRunner.Run($"index-memory --instance {newInstance} --from \"{memDir}\"");
            string dbPath = GrimoraCliRunner.InstanceDbPath(newInstance);
            string actual;
            using (SqliteConnection connection = StoreConnection.Open(dbPath))
            {
                actual = new ShedMemoryTool().ExecuteCli(connection, "shed-memory-fixture").Trim();
            }

            Assert.Equal(expected, actual);
            Assert.Equal("shed memory 'shed-memory-fixture'.", actual);

            using SqliteConnection check = new($"Data Source={dbPath};Mode=ReadOnly");
            check.Open();
            using SqliteCommand count = check.CreateCommand();
            count.CommandText = "SELECT count(*) FROM memory WHERE k='shed-memory-fixture'";
            Assert.Equal(0L, (long)(count.ExecuteScalar() ?? 0L));
        }
        finally
        {
            GrimoraCliRunner.DeleteInstance(oldInstance);
            GrimoraCliRunner.DeleteInstance(newInstance);
            Directory.Delete(memDir, recursive: true);
        }
    }

    [Fact]
    public void CliShapeReportsNoMemoryForAnUnknownKey()
    {
        string instance = GrimoraCliRunner.NewTestInstance("shed-memory-missing");
        try
        {
            GrimoraCliRunner.Run($"init --instance {instance}");

            (string stdout, int exitCode) = GrimoraCliRunner.Run($"shed-memory --instance {instance} --key never-existed");
            Assert.Equal(0, exitCode);
            string expected = stdout.Trim();

            string dbPath = GrimoraCliRunner.InstanceDbPath(instance);
            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = new ShedMemoryTool().ExecuteCli(connection, "never-existed").Trim();

            Assert.Equal(expected, actual);
            Assert.Equal("no memory 'never-existed'.", actual);
        }
        finally
        {
            GrimoraCliRunner.DeleteInstance(instance);
        }
    }

    [Fact]
    public void McpShapeMatchesTodaysMcpOutputAndDeletesTheRow()
    {
        string instance = GrimoraCliRunner.NewTestInstance("shed-memory-mcp-hit");
        string memDir = MakeMemoryDir();
        string? previousInstanceEnv = Environment.GetEnvironmentVariable("Grimora_INSTANCE");
        try
        {
            GrimoraCliRunner.Run($"init --instance {instance}");
            GrimoraCliRunner.Run($"index-memory --instance {instance} --from \"{memDir}\"");

            Environment.SetEnvironmentVariable("Grimora_INSTANCE", instance);
            string expected = (string)InvokeMcpShedMemory("shed-memory-fixture")!;

            // The MCP oracle already deleted the row; reindex to give the new tool the same starting state.
            GrimoraCliRunner.Run($"index-memory --instance {instance} --from \"{memDir}\"");
            string dbPath = GrimoraCliRunner.InstanceDbPath(instance);
            string actual;
            using (SqliteConnection connection = StoreConnection.Open(dbPath))
            {
                actual = new ShedMemoryTool().ExecuteMcp(connection, "shed-memory-fixture");
            }

            Assert.Equal(expected, actual);
            Assert.Equal("shed memory 'shed-memory-fixture'.", actual);
        }
        finally
        {
            Environment.SetEnvironmentVariable("Grimora_INSTANCE", previousInstanceEnv);
            GrimoraCliRunner.DeleteInstance(instance);
            Directory.Delete(memDir, recursive: true);
        }
    }

    [Fact]
    public void McpShapeReportsNoMemoryForAnUnknownKey()
    {
        string instance = GrimoraCliRunner.NewTestInstance("shed-memory-mcp-missing");
        string? previousInstanceEnv = Environment.GetEnvironmentVariable("Grimora_INSTANCE");
        try
        {
            GrimoraCliRunner.Run($"init --instance {instance}");

            Environment.SetEnvironmentVariable("Grimora_INSTANCE", instance);
            string expected = (string)InvokeMcpShedMemory("never-existed")!;

            string dbPath = GrimoraCliRunner.InstanceDbPath(instance);
            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = new ShedMemoryTool().ExecuteMcp(connection, "never-existed");

            Assert.Equal(expected, actual);
            Assert.Equal("no memory 'never-existed'.", actual);
        }
        finally
        {
            Environment.SetEnvironmentVariable("Grimora_INSTANCE", previousInstanceEnv);
            GrimoraCliRunner.DeleteInstance(instance);
        }
    }

    private static string MakeMemoryDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"grimora-shed-memory-fixture-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "shed-memory-fixture.md"),
            "---\ntype: feedback\nname: Shed Memory Fixture\ndescription: a memory to be shed\n---\n\nBody text.\n");
        return dir;
    }

    private static object? InvokeMcpShedMemory(string key)
    {
        Assembly mcp = Assembly.LoadFrom(FindMcpDll());
        Type tools = mcp.GetType("GrimoraTools") ?? throw new InvalidOperationException("GrimoraTools type not found in mcp.dll");
        MethodInfo method = tools.GetMethod("shed_memory", BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException("GrimoraTools.shed_memory not found in mcp.dll");
        return method.Invoke(null, [key]);
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
