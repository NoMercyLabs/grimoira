using System.Text.Json;
using Aitm.Hooks.Data;
using Aitm.Hooks.Tools;
using Aitm.TestSupport;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Aitm.Hooks.Tests;

/// <summary>
/// RESTRUCTURE.md slice 21 ("Hooks, part 2"): the 3 SessionEnd handlers (<see cref="SessionIndexChatTool"/>
/// from session-index.mjs, <see cref="SessionIndexDocsTool"/> from session-index-docs.mjs, and
/// <see cref="IndexCodeSessionEndTool"/> from the index-code SessionEnd slot) have no test today. Each
/// gets a success path plus the failure test the card asks for: a bad payload, or a locked store, never
/// blocks the session end (exit 0 / empty return, no output, fast).
/// </summary>
public class SessionEndIndexTests
{
    private static string NewTempProjectDir(string label)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"test-hooks-{label}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void ShortenBusyTimeoutAndHoldLock(string dbPath, out SqliteConnection locker, out SqliteTransaction lockTx)
    {
        locker = new SqliteConnection($"Data Source={dbPath}");
        locker.Open();
        lockTx = locker.BeginTransaction();
        using SqliteCommand hold = locker.CreateCommand();
        hold.Transaction = lockTx;
        hold.CommandText = "INSERT INTO meta(key,value) VALUES('session-end-lock-test','1')";
        hold.ExecuteNonQuery();
    }

    // --- session-index.mjs / SessionIndexChatTool ---

    [Fact]
    public void SessionIndexChatIndexesTheTranscriptIntoTheChatChannel()
    {
        string projectDir = NewTempProjectDir("chat-ok");
        string instance = HookPaths.ResolveInstance(projectDir);
        try
        {
            AitmCliRunner.Run($"init --instance {instance}");
            string transcriptPath = Path.Combine(projectDir, "t.jsonl");
            File.WriteAllLines(transcriptPath,
            [
                JsonSerializer.Serialize(new { type = "user", uuid = "u1", timestamp = "t1", message = new { content = "This is a real user message long enough to index." } }),
            ]);
            string payload = JsonSerializer.Serialize(new { transcript_path = transcriptPath, cwd = projectDir, session_id = "s1" });

            string result = SessionIndexChatTool.Execute(payload);

            Assert.Equal("", result);
            using SqliteConnection connection = new($"Data Source={HookPaths.DbPath(instance)};Mode=ReadOnly");
            connection.Open();
            using SqliteCommand count = connection.CreateCommand();
            count.CommandText = "SELECT count(*) FROM chat";
            Assert.Equal(1L, (long)count.ExecuteScalar()!);
        }
        finally
        {
            AitmCliRunner.DeleteInstance(instance);
            Directory.Delete(projectDir, recursive: true);
        }
    }

    [Fact]
    public void SessionIndexChatWithABadPayloadNeverBlocksSessionEnd()
    {
        string result = SessionIndexChatTool.Execute("{not json");
        Assert.Equal("", result);
    }

    [Fact]
    public void SessionIndexChatWithALockedStoreNeverBlocksSessionEnd()
    {
        string projectDir = NewTempProjectDir("chat-lock");
        string instance = HookPaths.ResolveInstance(projectDir);
        try
        {
            AitmCliRunner.Run($"init --instance {instance}");
            string transcriptPath = Path.Combine(projectDir, "t.jsonl");
            File.WriteAllLines(transcriptPath,
            [
                JsonSerializer.Serialize(new { type = "user", uuid = "u1", timestamp = "t1", message = new { content = "This is a real user message long enough to index." } }),
            ]);
            string payload = JsonSerializer.Serialize(new { transcript_path = transcriptPath, cwd = projectDir, session_id = "s1" });

            ShortenBusyTimeoutAndHoldLock(HookPaths.DbPath(instance), out SqliteConnection locker, out SqliteTransaction lockTx);
            DateTime start = DateTime.UtcNow;
            string result;
            try
            {
                result = SessionIndexChatTool.Execute(payload);
            }
            finally
            {
                lockTx.Rollback();
                locker.Dispose();
            }
            TimeSpan elapsed = DateTime.UtcNow - start;

            Assert.Equal("", result);
            Assert.True(elapsed < TimeSpan.FromSeconds(5), $"took {elapsed}");
        }
        finally
        {
            AitmCliRunner.DeleteInstance(instance);
            Directory.Delete(projectDir, recursive: true);
        }
    }

    // --- session-index-docs.mjs / SessionIndexDocsTool ---

    [Fact]
    public void SessionIndexDocsIndexesTheClaudeDirIntoTheDocsChannel()
    {
        string projectDir = NewTempProjectDir("docs-ok");
        string instance = HookPaths.ResolveInstance(projectDir);
        try
        {
            AitmCliRunner.Run($"init --instance {instance}");
            string claudeDir = Path.Combine(projectDir, ".claude");
            Directory.CreateDirectory(claudeDir);
            File.WriteAllText(Path.Combine(claudeDir, "spec.md"), "# A real spec\n\nEnough body text to survive the compaction filter.\n");
            string payload = JsonSerializer.Serialize(new { cwd = projectDir, session_id = "s1" });

            string result = SessionIndexDocsTool.Execute(payload);

            Assert.Equal("", result);
            using SqliteConnection connection = new($"Data Source={HookPaths.DbPath(instance)};Mode=ReadOnly");
            connection.Open();
            using SqliteCommand count = connection.CreateCommand();
            count.CommandText = "SELECT count(*) FROM docs";
            Assert.True((long)count.ExecuteScalar()! > 0);
        }
        finally
        {
            AitmCliRunner.DeleteInstance(instance);
            Directory.Delete(projectDir, recursive: true);
        }
    }

    [Fact]
    public void SessionIndexDocsWithABadPayloadNeverBlocksSessionEnd()
    {
        string result = SessionIndexDocsTool.Execute("not json at all");
        Assert.Equal("", result);
    }

    [Fact]
    public void SessionIndexDocsWithALockedStoreNeverBlocksSessionEnd()
    {
        string projectDir = NewTempProjectDir("docs-lock");
        string instance = HookPaths.ResolveInstance(projectDir);
        try
        {
            AitmCliRunner.Run($"init --instance {instance}");
            string claudeDir = Path.Combine(projectDir, ".claude");
            Directory.CreateDirectory(claudeDir);
            File.WriteAllText(Path.Combine(claudeDir, "spec.md"), "# A real spec\n\nEnough body text to survive the compaction filter.\n");
            string payload = JsonSerializer.Serialize(new { cwd = projectDir, session_id = "s1" });

            ShortenBusyTimeoutAndHoldLock(HookPaths.DbPath(instance), out SqliteConnection locker, out SqliteTransaction lockTx);
            DateTime start = DateTime.UtcNow;
            string result;
            try
            {
                result = SessionIndexDocsTool.Execute(payload);
            }
            finally
            {
                lockTx.Rollback();
                locker.Dispose();
            }
            TimeSpan elapsed = DateTime.UtcNow - start;

            Assert.Equal("", result);
            Assert.True(elapsed < TimeSpan.FromSeconds(5), $"took {elapsed}");
        }
        finally
        {
            AitmCliRunner.DeleteInstance(instance);
            Directory.Delete(projectDir, recursive: true);
        }
    }

    // --- index-code.mjs SessionEnd slot / IndexCodeSessionEndTool ---

    [Fact]
    public void IndexCodeSessionEndIndexesRegisteredProjectsIntoEdges()
    {
        string projectDir = NewTempProjectDir("code-ok");
        string instance = HookPaths.ResolveInstance(projectDir);
        string fixtureRoot = Path.Combine(Path.GetTempPath(), $"aitm-index-code-fixture-{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixtureRoot);
        try
        {
            AitmCliRunner.Run($"init --instance {instance}");
            File.WriteAllText(Path.Combine(fixtureRoot, "widget.ts"), "export class Widget {}\n");
            AitmCliRunner.Run($"project --instance {instance} --name web --root \"{fixtureRoot}\" --globs \"*.ts\"");
            string payload = JsonSerializer.Serialize(new { cwd = projectDir, session_id = "s1" });

            string result = IndexCodeSessionEndTool.Execute(payload);

            Assert.Equal("", result);
            using SqliteConnection connection = new($"Data Source={HookPaths.DbPath(instance)};Mode=ReadOnly");
            connection.Open();
            using SqliteCommand count = connection.CreateCommand();
            count.CommandText = "SELECT count(*) FROM edges WHERE symbol='Widget'";
            Assert.Equal(1L, (long)count.ExecuteScalar()!);
        }
        finally
        {
            AitmCliRunner.DeleteInstance(instance);
            Directory.Delete(projectDir, recursive: true);
            Directory.Delete(fixtureRoot, recursive: true);
        }
    }

    [Fact]
    public void IndexCodeSessionEndKeepsIndexingWhenARegexTimesOutOnOneFile()
    {
        // 60k blank lines make the python class/function patterns rescan the whitespace from every line start
        // (quadratic), so both outrun the shared match timeout on that file. A timeout means "no declaration
        // found in this file": the hook still exits 0 with no output AND indexes the next file.
        string projectDir = NewTempProjectDir("code-regex-timeout");
        string instance = HookPaths.ResolveInstance(projectDir);
        string fixtureRoot = Path.Combine(Path.GetTempPath(), $"aitm-index-code-timeout-{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixtureRoot);
        try
        {
            AitmCliRunner.Run($"init --instance {instance}");
            File.WriteAllText(Path.Combine(fixtureRoot, "a_blank_lines.py"), new string('\n', 60_000) + "class Lost:\n    pass\n");
            File.WriteAllText(Path.Combine(fixtureRoot, "b_kept.py"), "class Kept:\n    pass\n");
            AitmCliRunner.Run($"project --instance {instance} --name py --root \"{fixtureRoot}\" --globs \"*.py\"");
            string payload = JsonSerializer.Serialize(new { cwd = projectDir, session_id = "s1" });

            string result = IndexCodeSessionEndTool.Execute(payload);

            Assert.Equal("", result);
            using SqliteConnection connection = new($"Data Source={HookPaths.DbPath(instance)};Mode=ReadOnly");
            connection.Open();
            using SqliteCommand count = connection.CreateCommand();
            count.CommandText = "SELECT count(*) FROM edges WHERE symbol='Kept'";
            Assert.Equal(1L, (long)count.ExecuteScalar()!);
        }
        finally
        {
            AitmCliRunner.DeleteInstance(instance);
            Directory.Delete(projectDir, recursive: true);
            Directory.Delete(fixtureRoot, recursive: true);
        }
    }

    [Fact]
    public void IndexCodeSessionEndWithABadPayloadNeverBlocksSessionEnd()
    {
        string result = IndexCodeSessionEndTool.Execute("{{{not json");
        Assert.Equal("", result);
    }

    [Fact]
    public void IndexCodeSessionEndWithALockedStoreNeverBlocksSessionEnd()
    {
        string projectDir = NewTempProjectDir("code-lock");
        string instance = HookPaths.ResolveInstance(projectDir);
        try
        {
            AitmCliRunner.Run($"init --instance {instance}");
            string payload = JsonSerializer.Serialize(new { cwd = projectDir, session_id = "s1" });

            ShortenBusyTimeoutAndHoldLock(HookPaths.DbPath(instance), out SqliteConnection locker, out SqliteTransaction lockTx);
            DateTime start = DateTime.UtcNow;
            string result;
            try
            {
                result = IndexCodeSessionEndTool.Execute(payload);
            }
            finally
            {
                lockTx.Rollback();
                locker.Dispose();
            }
            TimeSpan elapsed = DateTime.UtcNow - start;

            Assert.Equal("", result);
            Assert.True(elapsed < TimeSpan.FromSeconds(5), $"took {elapsed}");
        }
        finally
        {
            AitmCliRunner.DeleteInstance(instance);
            Directory.Delete(projectDir, recursive: true);
        }
    }

    [Fact]
    public void SessionEndHandlersWithNoStoreForTheInstanceAreSilentNoOps()
    {
        string projectDir = NewTempProjectDir("no-store");
        try
        {
            string transcriptPath = Path.Combine(projectDir, "t.jsonl");
            File.WriteAllLines(transcriptPath, [JsonSerializer.Serialize(new { type = "user", message = new { content = "irrelevant" } })]);
            string chatPayload = JsonSerializer.Serialize(new { transcript_path = transcriptPath, cwd = projectDir, session_id = "s1" });
            string genericPayload = JsonSerializer.Serialize(new { cwd = projectDir, session_id = "s1" });

            Assert.Equal("", SessionIndexChatTool.Execute(chatPayload));
            Assert.Equal("", SessionIndexDocsTool.Execute(genericPayload));
            Assert.Equal("", IndexCodeSessionEndTool.Execute(genericPayload));
        }
        finally
        {
            Directory.Delete(projectDir, recursive: true);
        }
    }
}
