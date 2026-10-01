using Grimoira.Brain.Tools;
using Grimoira.Store.Data;
using Grimoira.TestSupport;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Grimoira.Brain.Tests;

/// <summary>
/// Reviewer finding (chatgpt/codex/gemini/vscode-chat, 2026-09-27): every session targeting one instance
/// used to share the single ledger file <c>pending-learn.jsonl</c>, so <c>brain_flush</c> committed the
/// WHOLE file — including another session's still-uncommitted staged entries, never what the calling
/// session asked for. <see cref="BrainStageTool.LedgerPath"/> now keys the ledger by
/// <c>(instance, session)</c> when a caller names its own session id, so two independent sessions staging
/// into the same instance get two independent ledgers, and a flush only ever commits its own.
/// </summary>
public class SessionSafeStagingLedgerTests
{
    [Fact]
    public void TwoSessionsStagingIntoOneInstanceGetIndependentLedgers()
    {
        string instance = GrimoiraCliRunner.NewTestInstance("session-safe-ledgers");
        try
        {
            GrimoiraCliRunner.Seed($"init --instance {instance}");
            using SqliteConnection connection = StoreConnection.Open(GrimoiraCliRunner.InstanceDbPath(instance));

            new BrainStageTool().ExecuteMcp(connection, "node", "session-a-node", "fact", "session A's own fact", sessionId: "session-a");
            new BrainStageTool().ExecuteMcp(connection, "node", "session-b-node", "fact", "session B's own fact", sessionId: "session-b");

            string ledgerA = BrainStageTool.LedgerPath(connection, "session-a");
            string ledgerB = BrainStageTool.LedgerPath(connection, "session-b");

            Assert.NotEqual(ledgerA, ledgerB);
            Assert.Contains("session-a-node", File.ReadAllText(ledgerA));
            Assert.Contains("session-b-node", File.ReadAllText(ledgerB));
            // Neither session's own ledger carries the other session's staged entry.
            Assert.DoesNotContain("session-b-node", File.ReadAllText(ledgerA));
            Assert.DoesNotContain("session-a-node", File.ReadAllText(ledgerB));
        }
        finally
        {
            GrimoiraCliRunner.DeleteInstance(instance);
        }
    }

    [Fact]
    public void FlushingOneSessionNeverCommitsOrTouchesAnotherSessionsStagedRows()
    {
        string instance = GrimoiraCliRunner.NewTestInstance("session-safe-flush");
        try
        {
            GrimoiraCliRunner.Seed($"init --instance {instance}");
            using SqliteConnection connection = StoreConnection.Open(GrimoiraCliRunner.InstanceDbPath(instance));

            new BrainStageTool().ExecuteMcp(connection, "node", "flush-session-a-node", "fact", "session A's own fact", sessionId: "flush-session-a");
            new BrainStageTool().ExecuteMcp(connection, "node", "flush-session-b-node", "fact", "session B's own fact", sessionId: "flush-session-b");
            string ledgerB = BrainStageTool.LedgerPath(connection, "flush-session-b");
            string ledgerBBefore = File.ReadAllText(ledgerB);

            // Session A flushes — session B never asked for this, and must be entirely unaffected: its
            // ledger file survives untouched, and its row must not land in the store either.
            string resultA = new BrainFlushTool().ExecuteMcp(connection, sessionId: "flush-session-a");

            Assert.StartsWith("flushed 1 learning", resultA);
            Assert.False(File.Exists(BrainStageTool.LedgerPath(connection, "flush-session-a")), "session A's own ledger should be cleared");
            Assert.True(File.Exists(ledgerB), "session B's ledger must survive a different session's flush");
            Assert.Equal(ledgerBBefore, File.ReadAllText(ledgerB));

            using SqliteCommand countA = connection.CreateCommand();
            countA.CommandText = "SELECT count(*) FROM node WHERE k='flush-session-a-node'";
            Assert.Equal(1L, (long)countA.ExecuteScalar()!);

            using SqliteCommand countB = connection.CreateCommand();
            countB.CommandText = "SELECT count(*) FROM node WHERE k='flush-session-b-node'";
            Assert.Equal(0L, (long)countB.ExecuteScalar()!);

            // Session B flushes its own, independently — proves the isolation runs both ways, not just
            // "whoever flushes first wins".
            string resultB = new BrainFlushTool().ExecuteMcp(connection, sessionId: "flush-session-b");
            Assert.StartsWith("flushed 1 learning", resultB);
            using SqliteCommand countBAfter = connection.CreateCommand();
            countBAfter.CommandText = "SELECT count(*) FROM node WHERE k='flush-session-b-node'";
            Assert.Equal(1L, (long)countBAfter.ExecuteScalar()!);
        }
        finally
        {
            GrimoiraCliRunner.DeleteInstance(instance);
        }
    }

    [Fact]
    public void AFlushWithASessionIdStillFindsAnEntryStagedWithNoSessionAtAll()
    {
        // Regression: once a real caller (McpBridge) mints a session id on every call, a session's own
        // ledger is always fresh and empty the first time it flushes. If brain_flush only ever looked at
        // its own session's file, an entry staged before session keying existed (or by a caller that
        // never got a session id — an old cached bridge, a bare `grimoira stage`) would be stuck in the
        // shared ledger forever: no session would ever look there again. brain_flush must fall back to the
        // shared ledger when its own is empty, so a stray legacy entry still gets flushed.
        string instance = GrimoiraCliRunner.NewTestInstance("session-safe-fallback");
        try
        {
            GrimoiraCliRunner.Seed($"init --instance {instance}");
            using SqliteConnection connection = StoreConnection.Open(GrimoiraCliRunner.InstanceDbPath(instance));

            // No sessionId: lands in the legacy shared ledger.
            new BrainStageTool().ExecuteMcp(connection, "node", "legacy-shared-node", "fact", "staged with no session");

            // A brand-new session, with its own (empty) ledger, flushes with no argument the caller
            // controls other than its own session id.
            string result = new BrainFlushTool().ExecuteMcp(connection, sessionId: "a-fresh-session-that-staged-nothing");

            Assert.Equal("flushed 1 learning(s) into the brain.", result);
            using SqliteCommand count = connection.CreateCommand();
            count.CommandText = "SELECT count(*) FROM node WHERE k='legacy-shared-node'";
            Assert.Equal(1L, (long)count.ExecuteScalar()!);
            Assert.False(File.Exists(BrainStageTool.LedgerPath(connection)), "the shared ledger should be cleared once flushed");
        }
        finally
        {
            GrimoiraCliRunner.DeleteInstance(instance);
        }
    }

    [Fact]
    public void NoSessionIdKeepsTheOriginalSharedLedgerUnchanged()
    {
        // Backward compatibility: a caller that never mentions a session (an older bridge, a bare CLI
        // stage/flush with no --session) must behave exactly as before session keying existed.
        string instance = GrimoiraCliRunner.NewTestInstance("session-safe-legacy");
        try
        {
            GrimoiraCliRunner.Seed($"init --instance {instance}");
            using SqliteConnection connection = StoreConnection.Open(GrimoiraCliRunner.InstanceDbPath(instance));

            string ledger = BrainStageTool.LedgerPath(connection);
            Assert.Equal("pending-learn.jsonl", Path.GetFileName(ledger));
            Assert.Equal(Path.GetDirectoryName(GrimoiraCliRunner.InstanceDbPath(instance)), Path.GetDirectoryName(ledger));
        }
        finally
        {
            GrimoiraCliRunner.DeleteInstance(instance);
        }
    }
}
