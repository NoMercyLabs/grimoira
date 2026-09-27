using System.Text.Json;
using Aitm.Hooks.Data;
using Aitm.Hooks.Tools;
using Aitm.TestSupport;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Aitm.Hooks.Tests;

/// <summary>
/// RESTRUCTURE.md slice 21 ("Hooks, part 2"): ports index-on-edit.test.mjs. The .mjs test mocked
/// <c>spawnSync</c> to pin the fail-open/no-leak contract around a prebuilt-CLI child process; this port
/// exercises the same routing rules and the same contract (never blocks or crashes the edit; a failure
/// never leaks detail) against the in-process call <see cref="IndexOnEditTool"/> makes instead, since
/// Aitm.Hooks (reference level 3) may call Aitm.Memory/Aitm.Docs directly (RESTRUCTURE.md section 1;
/// ReferenceDirectionTests), unlike the .mjs which had no such project to link against.
/// </summary>
public class IndexOnEditToolTests
{
    private static string NewTempProjectDir(string label)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"test-hooks-edit-{label}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static long ScalarCount(string dbPath, string table)
    {
        using SqliteConnection connection = new($"Data Source={dbPath};Mode=ReadOnly");
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT count(*) FROM {table}";
        return (long)command.ExecuteScalar()!;
    }

    [Fact]
    public void EditingAMemoryMarkdownFileReindexesTheMemoryChannel()
    {
        string projectDir = NewTempProjectDir("memory");
        string instance = HookPaths.ResolveInstance(projectDir);
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        // The encoded segment need only END with the instance slug, same guard the .mjs applies.
        string encoded = $"fixture-{instance}";
        string memoryDir = Path.Combine(home, ".claude", "projects", encoded, "memory");
        Directory.CreateDirectory(memoryDir);
        string editedFile = Path.Combine(memoryDir, "topic-example.md");
        File.WriteAllText(editedFile, "---\nname: Example topic\n---\nSome real body text about a hard rule.\n");
        try
        {
            AitmCliRunner.Run($"init --instance {instance}");
            string payload = JsonSerializer.Serialize(new { cwd = projectDir, tool_input = new { file_path = editedFile } });

            string result = IndexOnEditTool.Execute(payload);

            Assert.Equal("", result);
            Assert.Equal(1L, ScalarCount(HookPaths.DbPath(instance), "memory"));
        }
        finally
        {
            AitmCliRunner.DeleteInstance(instance);
            Directory.Delete(memoryDir, recursive: true);
            Directory.Delete(projectDir, recursive: true);
        }
    }

    [Fact]
    public void EditingMemoryMdItselfIsANoOp()
    {
        string projectDir = NewTempProjectDir("memory-md");
        string instance = HookPaths.ResolveInstance(projectDir);
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string encoded = $"fixture-{instance}";
        string memoryDir = Path.Combine(home, ".claude", "projects", encoded, "memory");
        Directory.CreateDirectory(memoryDir);
        string editedFile = Path.Combine(memoryDir, "MEMORY.md");
        File.WriteAllText(editedFile, "# index\n");
        try
        {
            AitmCliRunner.Run($"init --instance {instance}");
            string payload = JsonSerializer.Serialize(new { cwd = projectDir, tool_input = new { file_path = editedFile } });

            string result = IndexOnEditTool.Execute(payload);

            Assert.Equal("", result);
            Assert.Equal(0L, ScalarCount(HookPaths.DbPath(instance), "memory"));
        }
        finally
        {
            AitmCliRunner.DeleteInstance(instance);
            Directory.Delete(memoryDir, recursive: true);
            Directory.Delete(projectDir, recursive: true);
        }
    }

    [Fact]
    public void AMemoryEditUnderAnotherInstancesEncodedProjectIsNeverIndexed()
    {
        string projectDir = NewTempProjectDir("memory-cross");
        string instance = HookPaths.ResolveInstance(projectDir);
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        // Encoded segment ends with a DIFFERENT slug, so the guard must reject it.
        string encoded = "fixture-some-other-project";
        string memoryDir = Path.Combine(home, ".claude", "projects", encoded, "memory");
        Directory.CreateDirectory(memoryDir);
        string editedFile = Path.Combine(memoryDir, "topic-example.md");
        File.WriteAllText(editedFile, "Some real body text.\n");
        try
        {
            AitmCliRunner.Run($"init --instance {instance}");
            string payload = JsonSerializer.Serialize(new { cwd = projectDir, tool_input = new { file_path = editedFile } });

            string result = IndexOnEditTool.Execute(payload);

            Assert.Equal("", result);
            Assert.Equal(0L, ScalarCount(HookPaths.DbPath(instance), "memory"));
        }
        finally
        {
            AitmCliRunner.DeleteInstance(instance);
            Directory.Delete(memoryDir, recursive: true);
            Directory.Delete(projectDir, recursive: true);
        }
    }

    [Fact]
    public void EditingADesignSpecReindexesTheDocsChannel()
    {
        string projectDir = NewTempProjectDir("docs");
        string instance = HookPaths.ResolveInstance(projectDir);
        string designDir = Path.Combine(projectDir, ".claude", "docs", "design");
        Directory.CreateDirectory(designDir);
        string editedFile = Path.Combine(designDir, "spec.md");
        File.WriteAllText(editedFile, "# A real spec\n\nEnough body text to survive the compaction filter.\n");
        try
        {
            AitmCliRunner.Run($"init --instance {instance}");
            string payload = JsonSerializer.Serialize(new { cwd = projectDir, tool_input = new { file_path = editedFile } });

            string result = IndexOnEditTool.Execute(payload);

            Assert.Equal("", result);
            Assert.True(ScalarCount(HookPaths.DbPath(instance), "docs") > 0);
        }
        finally
        {
            AitmCliRunner.DeleteInstance(instance);
            Directory.Delete(projectDir, recursive: true);
        }
    }

    [Fact]
    public void EditingAnUnrelatedSourceFileIsASilentNoOp()
    {
        string projectDir = NewTempProjectDir("unrelated");
        string instance = HookPaths.ResolveInstance(projectDir);
        string editedFile = Path.Combine(projectDir, "src", "Widget.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(editedFile)!);
        File.WriteAllText(editedFile, "class Widget {}\n");
        try
        {
            AitmCliRunner.Run($"init --instance {instance}");
            string payload = JsonSerializer.Serialize(new { cwd = projectDir, tool_input = new { file_path = editedFile } });

            string result = IndexOnEditTool.Execute(payload);

            Assert.Equal("", result);
        }
        finally
        {
            AitmCliRunner.DeleteInstance(instance);
            Directory.Delete(projectDir, recursive: true);
        }
    }

    [Fact]
    public void ABadPayloadNeverBlocksOrCrashesTheEdit()
    {
        string result = IndexOnEditTool.Execute("not json");
        Assert.Equal("", result);
    }

    [Fact]
    public void NoFilePathInThePayloadIsASilentNoOp()
    {
        string result = IndexOnEditTool.Execute(JsonSerializer.Serialize(new { cwd = "C:/somewhere", tool_input = new { } }));
        Assert.Equal("", result);
    }

    [Fact]
    public void ALockedStoreOnAMemoryEditNeverBlocksOrCrashesTheEditAndLeaksNoDetail()
    {
        string projectDir = NewTempProjectDir("memory-lock");
        string instance = HookPaths.ResolveInstance(projectDir);
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string encoded = $"fixture-{instance}";
        string memoryDir = Path.Combine(home, ".claude", "projects", encoded, "memory");
        Directory.CreateDirectory(memoryDir);
        string editedFile = Path.Combine(memoryDir, "topic-example.md");
        File.WriteAllText(editedFile, "Some real body text mentioning a private detail that must never leak.\n");
        try
        {
            AitmCliRunner.Run($"init --instance {instance}");
            string payload = JsonSerializer.Serialize(new { cwd = projectDir, tool_input = new { file_path = editedFile } });

            using SqliteConnection locker = new($"Data Source={HookPaths.DbPath(instance)}");
            locker.Open();
            using SqliteTransaction lockTx = locker.BeginTransaction();
            using (SqliteCommand hold = locker.CreateCommand())
            {
                hold.Transaction = lockTx;
                hold.CommandText = "INSERT INTO meta(key,value) VALUES('edit-lock-test','1')";
                hold.ExecuteNonQuery();
            }

            DateTime start = DateTime.UtcNow;
            string result;
            try
            {
                result = IndexOnEditTool.Execute(payload);
            }
            finally
            {
                lockTx.Rollback();
                // ReSharper disable once DisposeOnUsingVariable (the lock must be released before the elapsed time is read)
                locker.Dispose();
            }
            TimeSpan elapsed = DateTime.UtcNow - start;

            Assert.Equal("", result);
            Assert.True(elapsed < TimeSpan.FromSeconds(5), $"took {elapsed}");
        }
        finally
        {
            AitmCliRunner.DeleteInstance(instance);
            Directory.Delete(memoryDir, recursive: true);
            Directory.Delete(projectDir, recursive: true);
        }
    }
}
