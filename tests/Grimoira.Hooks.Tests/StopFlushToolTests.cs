using System.Text.Json;
using Grimoira.Hooks.Data;
using Grimoira.Hooks.Tools;
using Grimoira.TestSupport;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Grimoira.Hooks.Tests;

/// <summary>
/// Reviewer finding (chatgpt/codex/gemini/vscode-chat, 2026-09-27): SKILL.md promises staged learning
/// "can't be silently dropped", but hooks.json had no Stop hook at all, so a session with unflushed
/// entries in <c>pending-learn.jsonl</c> could just end and nobody would ever know. <see cref="StopFlushTool"/>
/// is the Stop handler that closes that gap: flush what it can, warn about what it can't, and never stay
/// silent about a pending learning that survives past Stop.
/// </summary>
public class StopFlushToolTests
{
    private static string NewTempProjectDir(string label)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"test-stop-{label}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string LedgerPathFor(string instance) => Path.Combine(HookPaths.InstanceDir(instance), "pending-learn.jsonl");

    [Fact]
    public void NoLedgerFileIsASilentNoOp()
    {
        string projectDir = NewTempProjectDir("no-ledger");
        string instance = HookPaths.ResolveInstance(projectDir);
        try
        {
            GrimoiraCliRunner.Seed($"init --instance {instance}");
            string payload = JsonSerializer.Serialize(new { cwd = projectDir, session_id = "s1" });

            Assert.Equal("", StopFlushTool.Execute(payload));
        }
        finally
        {
            GrimoiraCliRunner.DeleteInstance(instance);
            Directory.Delete(projectDir, recursive: true);
        }
    }

    [Fact]
    public void AnEmptyLedgerFileIsASilentNoOp()
    {
        string projectDir = NewTempProjectDir("empty-ledger");
        string instance = HookPaths.ResolveInstance(projectDir);
        try
        {
            GrimoiraCliRunner.Seed($"init --instance {instance}");
            string ledger = LedgerPathFor(instance);
            Directory.CreateDirectory(Path.GetDirectoryName(ledger)!);
            File.WriteAllText(ledger, "\n\n");
            string payload = JsonSerializer.Serialize(new { cwd = projectDir, session_id = "s1" });

            Assert.Equal("", StopFlushTool.Execute(payload));
        }
        finally
        {
            GrimoiraCliRunner.DeleteInstance(instance);
            Directory.Delete(projectDir, recursive: true);
        }
    }

    [Fact]
    public void APendingLearningIsFlushedIntoTheStore()
    {
        string projectDir = NewTempProjectDir("flush-ok");
        string instance = HookPaths.ResolveInstance(projectDir);
        try
        {
            GrimoiraCliRunner.Seed($"init --instance {instance}");
            string ledger = LedgerPathFor(instance);
            Directory.CreateDirectory(Path.GetDirectoryName(ledger)!);
            File.WriteAllText(ledger,
                "{\"k\":\"node\",\"key\":\"stop-hook-test-node\",\"kind\":\"fact\",\"label\":\"a durable fact\",\"gloss\":\"\",\"scheme\":\"\",\"hard\":false}\n");
            string payload = JsonSerializer.Serialize(new { cwd = projectDir, session_id = "s1" });

            string result = StopFlushTool.Execute(payload);

            Assert.StartsWith("grimoira: flushed 1 learning", result);
            Assert.False(File.Exists(ledger), "the ledger should be cleared once flushed");
            using SqliteConnection connection = new($"Data Source={HookPaths.DbPath(instance)};Mode=ReadOnly");
            connection.Open();
            using SqliteCommand count = connection.CreateCommand();
            count.CommandText = "SELECT count(*) FROM node WHERE k='stop-hook-test-node'";
            Assert.Equal(1L, (long)count.ExecuteScalar()!);
        }
        finally
        {
            GrimoiraCliRunner.DeleteInstance(instance);
            Directory.Delete(projectDir, recursive: true);
        }
    }

    [Fact]
    public void APendingLearningWithNoStoreYetWarnsInsteadOfStayingSilent()
    {
        string projectDir = NewTempProjectDir("flush-no-store");
        string instance = HookPaths.ResolveInstance(projectDir);
        try
        {
            // Deliberately never `init` — the instance directory + ledger exist, but no grimoira.db.
            string ledger = LedgerPathFor(instance);
            Directory.CreateDirectory(Path.GetDirectoryName(ledger)!);
            File.WriteAllText(ledger,
                "{\"k\":\"node\",\"key\":\"stop-hook-test-node\",\"kind\":\"fact\",\"label\":\"a durable fact\",\"gloss\":\"\",\"scheme\":\"\",\"hard\":false}\n");
            string payload = JsonSerializer.Serialize(new { cwd = projectDir, session_id = "s1" });

            string result = StopFlushTool.Execute(payload);

            Assert.StartsWith("grimoira WARNING:", result);
            Assert.Contains("1 staged learning", result);
            Assert.True(File.Exists(ledger), "an unflushed ledger must survive so the next Stop can retry it");
        }
        finally
        {
            if (Directory.Exists(HookPaths.InstanceDir(instance))) Directory.Delete(HookPaths.InstanceDir(instance), recursive: true);
            Directory.Delete(projectDir, recursive: true);
        }
    }

    [Fact]
    public void ABadPayloadNeverBlocksStop()
    {
        string result = StopFlushTool.Execute("{not json");
        Assert.Equal("", result);
    }
}
