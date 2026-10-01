using Grimoira.Brain.Tools;
using Grimoira.Store.Data;
using Grimoira.TestSupport;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Grimoira.Brain.Tests;

public class BrainFlushToolTests
{
    // --- CLI shape: oracle is today's grimoira.cs FlushCmd (grimoira.cs:1954). None of this has a test today
    //     (RESTRUCTURE.md slice 18 note). ---

    [Fact]
    public void CliFlushReportsNothingStagedOnAFreshInstance()
    {
        string oldInstance = GrimoiraCliRunner.NewTestInstance("flush-cli-empty-old");
        string newInstance = GrimoiraCliRunner.NewTestInstance("flush-cli-empty-new");
        try
        {
            GrimoiraCliRunner.Run($"init --instance {oldInstance}");
            (string oldOut, int oldExit) = GrimoiraCliRunner.Run($"flush --instance {oldInstance}");
            Assert.Equal(0, oldExit);

            GrimoiraCliRunner.Run($"init --instance {newInstance}");
            using SqliteConnection connection = StoreConnection.Open(GrimoiraCliRunner.InstanceDbPath(newInstance));
            string actual = new BrainFlushTool().ExecuteCli(connection);

            Assert.Equal(oldOut.Trim(), actual);
            Assert.Equal("nothing staged.", actual);
        }
        finally
        {
            GrimoiraCliRunner.DeleteInstance(oldInstance);
            GrimoiraCliRunner.DeleteInstance(newInstance);
        }
    }

    [Fact]
    public void CliFlushCommitsEveryStagedRowAndClearsTheLedger()
    {
        string oldInstance = GrimoiraCliRunner.NewTestInstance("flush-cli-commit-old");
        string newInstance = GrimoiraCliRunner.NewTestInstance("flush-cli-commit-new");
        try
        {
            string oldDb = GrimoiraCliRunner.InstanceDbPath(oldInstance);
            GrimoiraCliRunner.Run($"init --instance {oldInstance}");
            BrainTestFixtures.InsertNode(oldDb, "flush:s1", "concept", "subject", "");
            BrainTestFixtures.InsertNode(oldDb, "flush:o1", "concept", "object", "");
            BrainTestFixtures.InsertNode(oldDb, "flush:frame1", "codekind", "frame one", "");
            GrimoiraCliRunner.Run($"stage node flush:n1 fact \"a widget\" --gloss \"widget gloss\" --instance {oldInstance}");
            GrimoiraCliRunner.Run($"stage triple flush:s1 related flush:o1 --instance {oldInstance}");
            GrimoiraCliRunner.Run($"stage slot flush:frame1 place somewhere --instance {oldInstance}");
            (string oldOut, int oldExit) = GrimoiraCliRunner.Run($"flush --instance {oldInstance}");
            Assert.Equal(0, oldExit);
            string oldLedgerPath = Path.Combine(GrimoiraCliRunner.InstanceDir(oldInstance), "pending-learn.jsonl");

            string newDb = GrimoiraCliRunner.InstanceDbPath(newInstance);
            GrimoiraCliRunner.Run($"init --instance {newInstance}");
            BrainTestFixtures.InsertNode(newDb, "flush:s1", "concept", "subject", "");
            BrainTestFixtures.InsertNode(newDb, "flush:o1", "concept", "object", "");
            BrainTestFixtures.InsertNode(newDb, "flush:frame1", "codekind", "frame one", "");
            using SqliteConnection connection = StoreConnection.Open(newDb);
            BrainStageTool stage = new();
            stage.ExecuteCli(connection, ["node", "flush:n1", "fact", "a widget"], gloss: "widget gloss");
            stage.ExecuteCli(connection, ["triple", "flush:s1", "related", "flush:o1"]);
            stage.ExecuteCli(connection, ["slot", "flush:frame1", "place", "somewhere"]);
            string actual = new BrainFlushTool().ExecuteCli(connection);

            Assert.Equal(oldOut.Trim(), actual);
            Assert.Equal("flushed 3 learning(s) into the brain.", actual);
            Assert.False(File.Exists(oldLedgerPath), "the oracle's own ledger should be gone after a successful flush");
            Assert.False(File.Exists(BrainStageTool.LedgerPath(connection)));

            using SqliteCommand nodeCount = connection.CreateCommand();
            nodeCount.CommandText = "SELECT count(*) FROM node_now WHERE k='flush:n1'";
            Assert.Equal(1L, (long)nodeCount.ExecuteScalar()!);

            using SqliteCommand tripleCount = connection.CreateCommand();
            tripleCount.CommandText = "SELECT count(*) FROM triple_now WHERE s='flush:s1' AND p='related' AND o='flush:o1'";
            Assert.Equal(1L, (long)tripleCount.ExecuteScalar()!);

            using SqliteCommand slotCount = connection.CreateCommand();
            slotCount.CommandText = "SELECT count(*) FROM slot_now WHERE frame_k='flush:frame1' AND name='place' AND value='somewhere'";
            Assert.Equal(1L, (long)slotCount.ExecuteScalar()!);

            using SqliteCommand mutationCount = connection.CreateCommand();
            mutationCount.CommandText = "SELECT count(*) FROM mutations WHERE why='flush'";
            Assert.Equal(3L, (long)mutationCount.ExecuteScalar()!);
        }
        finally
        {
            GrimoiraCliRunner.DeleteInstance(oldInstance);
            GrimoiraCliRunner.DeleteInstance(newInstance);
        }
    }

    // --- MCP shape: pinned against today's bin/mcp.dll GrimoiraTools.brain_flush (mcp.cs:967). ---

    [Fact]
    public void McpShapeReportsNothingStagedOnAFreshInstance()
    {
        string instance = GrimoiraCliRunner.NewTestInstance("flush-mcp-empty");
        string? previousInstanceEnv = Environment.GetEnvironmentVariable("GRIMOIRA_INSTANCE");
        try
        {
            GrimoiraCliRunner.Run($"init --instance {instance}");
            Environment.SetEnvironmentVariable("GRIMOIRA_INSTANCE", instance);
            string expected = (string)McpDll.Invoke("brain_flush")!;

            using SqliteConnection connection = StoreConnection.Open(GrimoiraCliRunner.InstanceDbPath(instance));
            string actual = new BrainFlushTool().ExecuteMcp(connection);

            Assert.Equal(expected, actual);
            Assert.Equal("nothing staged.", actual);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GRIMOIRA_INSTANCE", previousInstanceEnv);
            GrimoiraCliRunner.DeleteInstance(instance);
        }
    }

    [Fact]
    public void McpShapeCommitsEveryStagedRowAndClearsTheLedger()
    {
        string oldInstance = GrimoiraCliRunner.NewTestInstance("flush-mcp-commit-old");
        string newInstance = GrimoiraCliRunner.NewTestInstance("flush-mcp-commit-new");
        string? previousInstanceEnv = Environment.GetEnvironmentVariable("GRIMOIRA_INSTANCE");
        try
        {
            GrimoiraCliRunner.Run($"init --instance {oldInstance}");
            Environment.SetEnvironmentVariable("GRIMOIRA_INSTANCE", oldInstance);
            McpDll.Invoke("brain_stage", "node", "flush:mcp-n1", "fact", "a widget", "widget gloss", "", false);
            string expected = (string)McpDll.Invoke("brain_flush")!;

            string newDb = GrimoiraCliRunner.InstanceDbPath(newInstance);
            GrimoiraCliRunner.Run($"init --instance {newInstance}");
            using SqliteConnection connection = StoreConnection.Open(newDb);
            new BrainStageTool().ExecuteMcp(connection, "node", "flush:mcp-n1", "fact", "a widget", "widget gloss");
            string actual = new BrainFlushTool().ExecuteMcp(connection);

            Assert.Equal(expected, actual);
            Assert.Equal("flushed 1 learning(s) into the brain.", actual);
            Assert.False(File.Exists(BrainStageTool.LedgerPath(connection)));

            using SqliteCommand nodeCount = connection.CreateCommand();
            nodeCount.CommandText = "SELECT count(*) FROM node_now WHERE k='flush:mcp-n1'";
            Assert.Equal(1L, (long)nodeCount.ExecuteScalar()!);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GRIMOIRA_INSTANCE", previousInstanceEnv);
            GrimoiraCliRunner.DeleteInstance(oldInstance);
            GrimoiraCliRunner.DeleteInstance(newInstance);
        }
    }

    // --- Failure test, fix 668134a: "brain_flush deleted the whole ledger unconditionally, so a
    //     learning rejected on 'database is locked' was lost." A transient lock/busy reject must keep
    //     that line in the ledger for the next flush to retry, instead of dropping it with the rest. ---

    [Fact]
    public void McpFlushKeepsALockedLineInTheLedgerInsteadOfDroppingIt()
    {
        string instance = GrimoiraCliRunner.NewTestInstance("flush-mcp-lock");
        try
        {
            string dbPath = GrimoiraCliRunner.InstanceDbPath(instance);
            GrimoiraCliRunner.Run($"init --instance {instance}");

            // Shorten only this connection's retry wait so the held lock below is hit in well under a
            // second, instead of waiting out the real 30s pragma fix 668134a raised it to. A `PRAGMA
            // busy_timeout` run after Open has no effect on Microsoft.Data.Sqlite's own busy retry loop;
            // that loop is driven by the connection string's `Default Timeout` keyword (seconds) — see
            // src/Grimoira.Hooks/Data/HookStore.cs, proven in slice 21. Skip StoreConnection.ApplyPragmas
            // here: it re-runs `PRAGMA busy_timeout=30000`, which would put the native 30s wait straight
            // back. The db file already carries WAL from the `init` above.
            using SqliteConnection connection = new($"Data Source={dbPath};Foreign Keys=True;Default Timeout=1");
            connection.Open();

            string ledger = BrainStageTool.LedgerPath(connection);
            Directory.CreateDirectory(Path.GetDirectoryName(ledger)!);
            File.WriteAllText(ledger, "{\"k\":\"node\",\"key\":\"flush:lock1\",\"kind\":\"fact\",\"label\":\"a locked write\",\"gloss\":\"\",\"scheme\":\"\",\"hard\":false}\n");

            using SqliteConnection locker = new($"Data Source={dbPath}");
            locker.Open();
            using SqliteTransaction lockTx = locker.BeginTransaction();
            using (SqliteCommand holdLock = locker.CreateCommand())
            {
                holdLock.Transaction = lockTx;
                holdLock.CommandText = "INSERT INTO meta(key,value) VALUES('flush-lock-test','1')";
                holdLock.ExecuteNonQuery();
            }

            string result;
            try
            {
                result = new BrainFlushTool().ExecuteMcp(connection);
            }
            finally
            {
                lockTx.Rollback();
            }

            Assert.Contains("kept for retry", result);
            Assert.Contains("locked", result, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(ledger), "the ledger must survive a lock-failed flush");
            Assert.Contains("flush:lock1", File.ReadAllText(ledger));

            using SqliteCommand nodeCount = connection.CreateCommand();
            nodeCount.CommandText = "SELECT count(*) FROM node_now WHERE k='flush:lock1'";
            Assert.Equal(0L, (long)nodeCount.ExecuteScalar()!);
        }
        finally
        {
            GrimoiraCliRunner.DeleteInstance(instance);
        }
    }
}
