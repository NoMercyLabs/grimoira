using Grimora.Brain.Tools;
using Grimora.Store.Data;
using Grimora.TestSupport;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Grimora.Brain.Tests;

public class BrainFlushToolTests
{
    // --- CLI shape: oracle is today's grimora.cs FlushCmd (grimora.cs:1954). None of this has a test today
    //     (RESTRUCTURE.md slice 18 note). ---

    [Fact]
    public void CliFlushReportsNothingStagedOnAFreshInstance()
    {
        string oldInstance = GrimoraCliRunner.NewTestInstance("flush-cli-empty-old");
        string newInstance = GrimoraCliRunner.NewTestInstance("flush-cli-empty-new");
        try
        {
            GrimoraCliRunner.Run($"init --instance {oldInstance}");
            (string oldOut, int oldExit) = GrimoraCliRunner.Run($"flush --instance {oldInstance}");
            Assert.Equal(0, oldExit);

            GrimoraCliRunner.Run($"init --instance {newInstance}");
            using SqliteConnection connection = StoreConnection.Open(GrimoraCliRunner.InstanceDbPath(newInstance));
            string actual = new BrainFlushTool().ExecuteCli(connection);

            Assert.Equal(oldOut.Trim(), actual);
            Assert.Equal("nothing staged.", actual);
        }
        finally
        {
            GrimoraCliRunner.DeleteInstance(oldInstance);
            GrimoraCliRunner.DeleteInstance(newInstance);
        }
    }

    [Fact]
    public void CliFlushCommitsEveryStagedRowAndClearsTheLedger()
    {
        string oldInstance = GrimoraCliRunner.NewTestInstance("flush-cli-commit-old");
        string newInstance = GrimoraCliRunner.NewTestInstance("flush-cli-commit-new");
        try
        {
            string oldDb = GrimoraCliRunner.InstanceDbPath(oldInstance);
            GrimoraCliRunner.Run($"init --instance {oldInstance}");
            BrainTestFixtures.InsertNode(oldDb, "flush:s1", "concept", "subject", "");
            BrainTestFixtures.InsertNode(oldDb, "flush:o1", "concept", "object", "");
            BrainTestFixtures.InsertNode(oldDb, "flush:frame1", "codekind", "frame one", "");
            GrimoraCliRunner.Run($"stage node flush:n1 fact \"a widget\" --gloss \"widget gloss\" --instance {oldInstance}");
            GrimoraCliRunner.Run($"stage triple flush:s1 related flush:o1 --instance {oldInstance}");
            GrimoraCliRunner.Run($"stage slot flush:frame1 place somewhere --instance {oldInstance}");
            (string oldOut, int oldExit) = GrimoraCliRunner.Run($"flush --instance {oldInstance}");
            Assert.Equal(0, oldExit);
            string oldLedgerPath = Path.Combine(GrimoraCliRunner.InstanceDir(oldInstance), "pending-learn.jsonl");

            string newDb = GrimoraCliRunner.InstanceDbPath(newInstance);
            GrimoraCliRunner.Run($"init --instance {newInstance}");
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
            GrimoraCliRunner.DeleteInstance(oldInstance);
            GrimoraCliRunner.DeleteInstance(newInstance);
        }
    }

    // --- MCP shape: pinned against today's bin/mcp.dll GrimoraTools.brain_flush (mcp.cs:967). ---

    [Fact]
    public void McpShapeReportsNothingStagedOnAFreshInstance()
    {
        string instance = GrimoraCliRunner.NewTestInstance("flush-mcp-empty");
        string? previousInstanceEnv = Environment.GetEnvironmentVariable("GRIMORA_INSTANCE");
        try
        {
            GrimoraCliRunner.Run($"init --instance {instance}");
            Environment.SetEnvironmentVariable("GRIMORA_INSTANCE", instance);
            string expected = (string)McpDll.Invoke("brain_flush")!;

            using SqliteConnection connection = StoreConnection.Open(GrimoraCliRunner.InstanceDbPath(instance));
            string actual = new BrainFlushTool().ExecuteMcp(connection);

            Assert.Equal(expected, actual);
            Assert.Equal("nothing staged.", actual);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GRIMORA_INSTANCE", previousInstanceEnv);
            GrimoraCliRunner.DeleteInstance(instance);
        }
    }

    [Fact]
    public void McpShapeCommitsEveryStagedRowAndClearsTheLedger()
    {
        string oldInstance = GrimoraCliRunner.NewTestInstance("flush-mcp-commit-old");
        string newInstance = GrimoraCliRunner.NewTestInstance("flush-mcp-commit-new");
        string? previousInstanceEnv = Environment.GetEnvironmentVariable("GRIMORA_INSTANCE");
        try
        {
            GrimoraCliRunner.Run($"init --instance {oldInstance}");
            Environment.SetEnvironmentVariable("GRIMORA_INSTANCE", oldInstance);
            McpDll.Invoke("brain_stage", "node", "flush:mcp-n1", "fact", "a widget", "widget gloss", "", false);
            string expected = (string)McpDll.Invoke("brain_flush")!;

            string newDb = GrimoraCliRunner.InstanceDbPath(newInstance);
            GrimoraCliRunner.Run($"init --instance {newInstance}");
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
            Environment.SetEnvironmentVariable("GRIMORA_INSTANCE", previousInstanceEnv);
            GrimoraCliRunner.DeleteInstance(oldInstance);
            GrimoraCliRunner.DeleteInstance(newInstance);
        }
    }

    // --- Failure test, fix 668134a: "brain_flush deleted the whole ledger unconditionally, so a
    //     learning rejected on 'database is locked' was lost." A transient lock/busy reject must keep
    //     that line in the ledger for the next flush to retry, instead of dropping it with the rest. ---

    [Fact]
    public void McpFlushKeepsALockedLineInTheLedgerInsteadOfDroppingIt()
    {
        string instance = GrimoraCliRunner.NewTestInstance("flush-mcp-lock");
        try
        {
            string dbPath = GrimoraCliRunner.InstanceDbPath(instance);
            GrimoraCliRunner.Run($"init --instance {instance}");

            // Shorten only this connection's retry wait so the held lock below is hit in well under a
            // second, instead of waiting out the real 30s pragma fix 668134a raised it to. A `PRAGMA
            // busy_timeout` run after Open has no effect on Microsoft.Data.Sqlite's own busy retry loop;
            // that loop is driven by the connection string's `Default Timeout` keyword (seconds) — see
            // src/Grimora.Hooks/Data/HookStore.cs, proven in slice 21. Skip StoreConnection.ApplyPragmas
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
            GrimoraCliRunner.DeleteInstance(instance);
        }
    }
}
