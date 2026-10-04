using System.Text.Json;
using Grimoira.Hooks.Data;
using Grimoira.Hooks.Tools;
using Grimoira.TestSupport;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Grimoira.Hooks.Tests;

/// <summary>
/// RESTRUCTURE.md slice 21 ("Hooks, part 2"): ports index-on-edit.test.mjs. The .mjs test mocked
/// <c>spawnSync</c> to pin the fail-open/no-leak contract around a prebuilt-CLI child process; this port
/// exercises the same routing rules and the same contract (never blocks or crashes the edit; a failure
/// never leaks detail) against the in-process call <see cref="IndexOnEditTool"/> makes instead, since
/// Grimoira.Hooks (reference level 3) may call Grimoira.Memory/Grimoira.Docs directly (RESTRUCTURE.md section 1;
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
            GrimoiraCliRunner.Run($"init --instance {instance}");
            string payload = JsonSerializer.Serialize(new { cwd = projectDir, tool_input = new { file_path = editedFile } });

            string result = IndexOnEditTool.Execute(payload);

            Assert.Equal("", result);
            Assert.Equal(1L, ScalarCount(HookPaths.DbPath(instance), "memory"));
        }
        finally
        {
            GrimoiraCliRunner.DeleteInstance(instance);
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
            GrimoiraCliRunner.Run($"init --instance {instance}");
            string payload = JsonSerializer.Serialize(new { cwd = projectDir, tool_input = new { file_path = editedFile } });

            string result = IndexOnEditTool.Execute(payload);

            Assert.Equal("", result);
            Assert.Equal(0L, ScalarCount(HookPaths.DbPath(instance), "memory"));
        }
        finally
        {
            GrimoiraCliRunner.DeleteInstance(instance);
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
            GrimoiraCliRunner.Run($"init --instance {instance}");
            string payload = JsonSerializer.Serialize(new { cwd = projectDir, tool_input = new { file_path = editedFile } });

            string result = IndexOnEditTool.Execute(payload);

            Assert.Equal("", result);
            Assert.Equal(0L, ScalarCount(HookPaths.DbPath(instance), "memory"));
        }
        finally
        {
            GrimoiraCliRunner.DeleteInstance(instance);
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
            GrimoiraCliRunner.Run($"init --instance {instance}");
            string payload = JsonSerializer.Serialize(new { cwd = projectDir, tool_input = new { file_path = editedFile } });

            string result = IndexOnEditTool.Execute(payload);

            Assert.Equal("", result);
            Assert.True(ScalarCount(HookPaths.DbPath(instance), "docs") > 0);
        }
        finally
        {
            GrimoiraCliRunner.DeleteInstance(instance);
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
            GrimoiraCliRunner.Run($"init --instance {instance}");
            string payload = JsonSerializer.Serialize(new { cwd = projectDir, tool_input = new { file_path = editedFile } });

            string result = IndexOnEditTool.Execute(payload);

            Assert.Equal("", result);
        }
        finally
        {
            GrimoiraCliRunner.DeleteInstance(instance);
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
            GrimoiraCliRunner.Run($"init --instance {instance}");
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
            GrimoiraCliRunner.DeleteInstance(instance);
            Directory.Delete(memoryDir, recursive: true);
            Directory.Delete(projectDir, recursive: true);
        }
    }

    private static string NormalizedDocPath(string file) => Path.GetFullPath(file).Replace('\\', '/').ToLowerInvariant();

    private static long DocRowsForPath(string dbPath, string file)
    {
        using SqliteConnection connection = new($"Data Source={dbPath};Mode=ReadOnly");
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM docs WHERE lower(replace(path,'\\','/')) = $p";
        command.Parameters.AddWithValue("$p", NormalizedDocPath(file));
        return (long)command.ExecuteScalar()!;
    }

    private static string DocContentForPath(string dbPath, string file)
    {
        using SqliteConnection connection = new($"Data Source={dbPath};Mode=ReadOnly");
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT group_concat(content, ' | ') FROM docs WHERE lower(replace(path,'\\','/')) = $p";
        command.Parameters.AddWithValue("$p", NormalizedDocPath(file));
        return command.ExecuteScalar() as string ?? "";
    }

    private const string KnownBody = "# Known plan\n\nEnough body text in the already indexed document to survive the compaction filter.\n";

    [Fact]
    public void EditingAMarkdownFileInAFolderTheStoreAlreadyHoldsIndexesThatFile()
    {
        string projectDir = NewTempProjectDir("known-folder");
        string instance = HookPaths.ResolveInstance(projectDir);
        string plansDir = Path.Combine(projectDir, "notes", "plans");
        Directory.CreateDirectory(plansDir);
        string knownFile = Path.Combine(plansDir, "known.md");
        File.WriteAllText(knownFile, KnownBody);
        string editedFile = Path.Combine(plansDir, "new-sibling.md");
        File.WriteAllText(editedFile, "# New sibling\n\nA fresh document written next to one the store already holds.\n");
        try
        {
            GrimoiraCliRunner.Seed($"init --instance {instance}");
            GrimoiraCliRunner.Seed($"index-docs --instance {instance} --from \"{knownFile}\"");
            Assert.Equal(1L, DocRowsForPath(HookPaths.DbPath(instance), knownFile));
            string payload = JsonSerializer.Serialize(new { cwd = projectDir, tool_input = new { file_path = editedFile } });

            string result = IndexOnEditTool.Execute(payload);

            Assert.Equal("", result);
            Assert.Equal(1L, DocRowsForPath(HookPaths.DbPath(instance), editedFile));
        }
        finally
        {
            GrimoiraCliRunner.DeleteInstance(instance);
            Directory.Delete(projectDir, recursive: true);
        }
    }

    [Fact]
    public void EditingAnAlreadyIndexedMarkdownFileReplacesItsContent()
    {
        string projectDir = NewTempProjectDir("known-file");
        string instance = HookPaths.ResolveInstance(projectDir);
        string plansDir = Path.Combine(projectDir, "notes", "plans");
        Directory.CreateDirectory(plansDir);
        string editedFile = Path.Combine(plansDir, "plan.md");
        File.WriteAllText(editedFile, "# Plan\n\nOriginal first section with enough words to be kept.\n\n## Second\n\nOriginal second section with enough words to be kept.\n");
        try
        {
            GrimoiraCliRunner.Seed($"init --instance {instance}");
            GrimoiraCliRunner.Seed($"index-docs --instance {instance} --from \"{editedFile}\"");
            Assert.Equal(2L, DocRowsForPath(HookPaths.DbPath(instance), editedFile));
            File.WriteAllText(editedFile, "# Plan\n\nRewritten single section carrying the updated decision text.\n");
            string payload = JsonSerializer.Serialize(new { cwd = projectDir, tool_input = new { file_path = editedFile } });

            string result = IndexOnEditTool.Execute(payload);

            Assert.Equal("", result);
            string content = DocContentForPath(HookPaths.DbPath(instance), editedFile);
            Assert.Contains("Rewritten single section", content);
            Assert.DoesNotContain("Original", content);
            Assert.Equal(1L, DocRowsForPath(HookPaths.DbPath(instance), editedFile));
        }
        finally
        {
            GrimoiraCliRunner.DeleteInstance(instance);
            Directory.Delete(projectDir, recursive: true);
        }
    }

    [Fact]
    public void EditingAMarkdownFileInAFolderTheStoreDoesNotHoldIsASilentNoOp()
    {
        string projectDir = NewTempProjectDir("unknown-folder");
        string instance = HookPaths.ResolveInstance(projectDir);
        string knownDir = Path.Combine(projectDir, "notes", "plans");
        string otherDir = Path.Combine(projectDir, "notes", "scratch");
        Directory.CreateDirectory(knownDir);
        Directory.CreateDirectory(otherDir);
        string knownFile = Path.Combine(knownDir, "known.md");
        File.WriteAllText(knownFile, KnownBody);
        string editedFile = Path.Combine(otherDir, "draft.md");
        File.WriteAllText(editedFile, "# Draft\n\nA document in a folder the store has never seen before.\n");
        try
        {
            GrimoiraCliRunner.Seed($"init --instance {instance}");
            GrimoiraCliRunner.Seed($"index-docs --instance {instance} --from \"{knownFile}\"");
            string payload = JsonSerializer.Serialize(new { cwd = projectDir, tool_input = new { file_path = editedFile } });

            string result = IndexOnEditTool.Execute(payload);

            Assert.Equal("", result);
            Assert.Equal(0L, DocRowsForPath(HookPaths.DbPath(instance), editedFile));
            Assert.Equal(1L, ScalarCount(HookPaths.DbPath(instance), "docs"));
        }
        finally
        {
            GrimoiraCliRunner.DeleteInstance(instance);
            Directory.Delete(projectDir, recursive: true);
        }
    }

    [Fact]
    public void EditingANonMarkdownFileInAFolderTheStoreHoldsIsASilentNoOp()
    {
        string projectDir = NewTempProjectDir("known-folder-non-md");
        string instance = HookPaths.ResolveInstance(projectDir);
        string plansDir = Path.Combine(projectDir, "notes", "plans");
        Directory.CreateDirectory(plansDir);
        string knownFile = Path.Combine(plansDir, "known.md");
        File.WriteAllText(knownFile, KnownBody);
        string editedFile = Path.Combine(plansDir, "data.json");
        File.WriteAllText(editedFile, "{ \"title\": \"Not markdown, with enough text to pass any length filter\" }\n");
        try
        {
            GrimoiraCliRunner.Seed($"init --instance {instance}");
            GrimoiraCliRunner.Seed($"index-docs --instance {instance} --from \"{knownFile}\"");
            string payload = JsonSerializer.Serialize(new { cwd = projectDir, tool_input = new { file_path = editedFile } });

            string result = IndexOnEditTool.Execute(payload);

            Assert.Equal("", result);
            Assert.Equal(0L, DocRowsForPath(HookPaths.DbPath(instance), editedFile));
            Assert.Equal(1L, ScalarCount(HookPaths.DbPath(instance), "docs"));
        }
        finally
        {
            GrimoiraCliRunner.DeleteInstance(instance);
            Directory.Delete(projectDir, recursive: true);
        }
    }
}
