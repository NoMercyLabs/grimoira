using System.Reflection;
using Aitm.Memory.Tools;
using Aitm.Store.Data;
using Aitm.TestSupport;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Aitm.Memory.Tests;

public class ShedMemoryToolTests
{
    [Fact]
    public void CliShapeMatchesTodaysCliOutputAndDeletesTheRow()
    {
        string oldInstance = AitmCliRunner.NewTestInstance("shed-memory-old");
        string newInstance = AitmCliRunner.NewTestInstance("shed-memory-new");
        string memDir = MakeMemoryDir();
        try
        {
            // Oracle: today's aitm.cs ShedMemory() (aitm.cs:1175).
            AitmCliRunner.Run($"init --instance {oldInstance}");
            AitmCliRunner.Run($"index-memory --instance {oldInstance} --from \"{memDir}\"");
            (string stdout, int exitCode) = AitmCliRunner.Run($"shed-memory --instance {oldInstance} --key shed-memory-fixture");
            Assert.Equal(0, exitCode);
            string expected = stdout.Trim();

            // New: ShedMemoryTool.
            AitmCliRunner.Run($"init --instance {newInstance}");
            AitmCliRunner.Run($"index-memory --instance {newInstance} --from \"{memDir}\"");
            string dbPath = AitmCliRunner.InstanceDbPath(newInstance);
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
            AitmCliRunner.DeleteInstance(oldInstance);
            AitmCliRunner.DeleteInstance(newInstance);
            Directory.Delete(memDir, recursive: true);
        }
    }

    [Fact]
    public void CliShapeReportsNoMemoryForAnUnknownKey()
    {
        string instance = AitmCliRunner.NewTestInstance("shed-memory-missing");
        try
        {
            AitmCliRunner.Run($"init --instance {instance}");

            (string stdout, int exitCode) = AitmCliRunner.Run($"shed-memory --instance {instance} --key never-existed");
            Assert.Equal(0, exitCode);
            string expected = stdout.Trim();

            string dbPath = AitmCliRunner.InstanceDbPath(instance);
            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = new ShedMemoryTool().ExecuteCli(connection, "never-existed").Trim();

            Assert.Equal(expected, actual);
            Assert.Equal("no memory 'never-existed'.", actual);
        }
        finally
        {
            AitmCliRunner.DeleteInstance(instance);
        }
    }

    [Fact]
    public void McpShapeMatchesTodaysMcpOutputAndDeletesTheRow()
    {
        string instance = AitmCliRunner.NewTestInstance("shed-memory-mcp-hit");
        string memDir = MakeMemoryDir();
        string? previousInstanceEnv = Environment.GetEnvironmentVariable("AITM_INSTANCE");
        try
        {
            AitmCliRunner.Run($"init --instance {instance}");
            AitmCliRunner.Run($"index-memory --instance {instance} --from \"{memDir}\"");

            Environment.SetEnvironmentVariable("AITM_INSTANCE", instance);
            string expected = (string)InvokeMcpShedMemory("shed-memory-fixture")!;

            // The MCP oracle already deleted the row; reindex to give the new tool the same starting state.
            AitmCliRunner.Run($"index-memory --instance {instance} --from \"{memDir}\"");
            string dbPath = AitmCliRunner.InstanceDbPath(instance);
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
            Environment.SetEnvironmentVariable("AITM_INSTANCE", previousInstanceEnv);
            AitmCliRunner.DeleteInstance(instance);
            Directory.Delete(memDir, recursive: true);
        }
    }

    [Fact]
    public void McpShapeReportsNoMemoryForAnUnknownKey()
    {
        string instance = AitmCliRunner.NewTestInstance("shed-memory-mcp-missing");
        string? previousInstanceEnv = Environment.GetEnvironmentVariable("AITM_INSTANCE");
        try
        {
            AitmCliRunner.Run($"init --instance {instance}");

            Environment.SetEnvironmentVariable("AITM_INSTANCE", instance);
            string expected = (string)InvokeMcpShedMemory("never-existed")!;

            string dbPath = AitmCliRunner.InstanceDbPath(instance);
            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = new ShedMemoryTool().ExecuteMcp(connection, "never-existed");

            Assert.Equal(expected, actual);
            Assert.Equal("no memory 'never-existed'.", actual);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AITM_INSTANCE", previousInstanceEnv);
            AitmCliRunner.DeleteInstance(instance);
        }
    }

    private static string MakeMemoryDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"aitm-shed-memory-fixture-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "shed-memory-fixture.md"),
            "---\ntype: feedback\nname: Shed Memory Fixture\ndescription: a memory to be shed\n---\n\nBody text.\n");
        return dir;
    }

    private static object? InvokeMcpShedMemory(string key)
    {
        Assembly mcp = Assembly.LoadFrom(FindMcpDll());
        Type tools = mcp.GetType("AitmTools") ?? throw new InvalidOperationException("AitmTools type not found in mcp.dll");
        MethodInfo method = tools.GetMethod("shed_memory", BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException("AitmTools.shed_memory not found in mcp.dll");
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
