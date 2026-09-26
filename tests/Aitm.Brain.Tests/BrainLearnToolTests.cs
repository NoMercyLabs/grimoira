using Aitm.Brain.Tools;
using Aitm.Store.Data;
using Aitm.TestSupport;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Aitm.Brain.Tests;

public class BrainLearnToolTests
{
    // --- MCP shape: pinned against today's bin/mcp.dll AitmTools.brain_learn (mcp.cs:782). ---

    // Every write test below seeds TWO instances (old/new) rather than sharing one, the same way
    // BrainRecallToolTests does: opening a connection through mcp.cs's own Open() also runs its
    // MaybeMaintain() (mcp.cs:206), which backfills every null-scheme node's scheme to its kind the
    // instant a fresh store is 3+ "days" (i.e. never-maintained) old. Sharing one db between the oracle
    // call and the new tool's call would let that side effect leak into the state the new tool then
    // reads, so oracle and tool each get their own independently-seeded store.

    [Fact]
    public void McpShapeLearnsANewNodeAndLogsTheMutation()
    {
        string oldInstance = AitmCliRunner.NewTestInstance("brain-learn-mcp-node-new-old");
        string newInstance = AitmCliRunner.NewTestInstance("brain-learn-mcp-node-new-new");
        string? previousInstanceEnv = Environment.GetEnvironmentVariable("AITM_INSTANCE");
        try
        {
            AitmCliRunner.Run($"init --instance {oldInstance}");
            Environment.SetEnvironmentVariable("AITM_INSTANCE", oldInstance);
            string expected = (string)McpDll.Invoke("brain_learn", "node", "learn:n1", "concept", "a widget", "widget gloss", "", false)!;

            string newDb = AitmCliRunner.InstanceDbPath(newInstance);
            AitmCliRunner.Run($"init --instance {newInstance}");
            using SqliteConnection connection = StoreConnection.Open(newDb);
            string actual = new BrainLearnTool().ExecuteMcp(connection, "node", "learn:n1", "concept", "a widget", "widget gloss");

            Assert.Equal(expected, actual);
            Assert.Equal("node learned.", actual);

            using SqliteCommand nodeRow = connection.CreateCommand();
            nodeRow.CommandText = "SELECT kind, label, gloss FROM node_now WHERE k='learn:n1'";
            using SqliteDataReader reader = nodeRow.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal("concept", reader.GetString(0));
            Assert.Equal("a widget", reader.GetString(1));
            Assert.Equal("widget gloss", reader.GetString(2));
            reader.Close();

            using SqliteCommand mutation = connection.CreateCommand();
            mutation.CommandText = "SELECT count(*) FROM mutations WHERE kind='node' AND k='learn:n1' AND op='insert' AND why='brain_learn'";
            Assert.Equal(1L, (long)mutation.ExecuteScalar()!);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AITM_INSTANCE", previousInstanceEnv);
            AitmCliRunner.DeleteInstance(oldInstance);
            AitmCliRunner.DeleteInstance(newInstance);
        }
    }

    [Fact]
    public void McpShapeReportsNoopWhenTheNodeIsUnchanged()
    {
        string oldInstance = AitmCliRunner.NewTestInstance("brain-learn-mcp-node-noop-old");
        string newInstance = AitmCliRunner.NewTestInstance("brain-learn-mcp-node-noop-new");
        string? previousInstanceEnv = Environment.GetEnvironmentVariable("AITM_INSTANCE");
        try
        {
            string oldDb = AitmCliRunner.InstanceDbPath(oldInstance);
            AitmCliRunner.Run($"init --instance {oldInstance}");
            BrainTestFixtures.InsertNode(oldDb, "learn:n2", "concept", "a widget", "widget gloss");

            Environment.SetEnvironmentVariable("AITM_INSTANCE", oldInstance);
            string expected = (string)McpDll.Invoke("brain_learn", "node", "learn:n2", "concept", "a widget", "widget gloss", "", false)!;

            string newDb = AitmCliRunner.InstanceDbPath(newInstance);
            AitmCliRunner.Run($"init --instance {newInstance}");
            BrainTestFixtures.InsertNode(newDb, "learn:n2", "concept", "a widget", "widget gloss");
            using SqliteConnection connection = StoreConnection.Open(newDb);
            string actual = new BrainLearnTool().ExecuteMcp(connection, "node", "learn:n2", "concept", "a widget", "widget gloss");

            Assert.Equal(expected, actual);
            Assert.Equal("noop (unchanged).", actual);

            using SqliteCommand mutation = connection.CreateCommand();
            mutation.CommandText = "SELECT count(*) FROM mutations WHERE kind='node' AND k='learn:n2'";
            Assert.Equal(0L, (long)mutation.ExecuteScalar()!);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AITM_INSTANCE", previousInstanceEnv);
            AitmCliRunner.DeleteInstance(oldInstance);
            AitmCliRunner.DeleteInstance(newInstance);
        }
    }

    [Fact]
    public void McpShapeRejectsAProseNodeKindWithTheRicherMcpGuardText()
    {
        string instance = AitmCliRunner.NewTestInstance("brain-learn-mcp-guard");
        string? previousInstanceEnv = Environment.GetEnvironmentVariable("AITM_INSTANCE");
        try
        {
            // A rejection writes nothing, so sharing one instance is safe here.
            string dbPath = AitmCliRunner.InstanceDbPath(instance);
            AitmCliRunner.Run($"init --instance {instance}");

            Environment.SetEnvironmentVariable("AITM_INSTANCE", instance);
            string expected = (string)McpDll.Invoke("brain_learn", "node", "learn:bad", "This is prose", "some label", "", "", false)!;

            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = new BrainLearnTool().ExecuteMcp(connection, "node", "learn:bad", "This is prose", "some label");

            Assert.Equal(expected, actual);
            Assert.StartsWith("rejected:", actual);
            Assert.Contains("is not a node kind", actual);

            using SqliteCommand nodeCount = connection.CreateCommand();
            nodeCount.CommandText = "SELECT count(*) FROM node_now WHERE k='learn:bad'";
            Assert.Equal(0L, (long)nodeCount.ExecuteScalar()!);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AITM_INSTANCE", previousInstanceEnv);
            AitmCliRunner.DeleteInstance(instance);
        }
    }

    [Fact]
    public void McpShapeLearnsANewTripleAndLogsTheMutation()
    {
        string oldInstance = AitmCliRunner.NewTestInstance("brain-learn-mcp-triple-new-old");
        string newInstance = AitmCliRunner.NewTestInstance("brain-learn-mcp-triple-new-new");
        string? previousInstanceEnv = Environment.GetEnvironmentVariable("AITM_INSTANCE");
        try
        {
            string oldDb = AitmCliRunner.InstanceDbPath(oldInstance);
            AitmCliRunner.Run($"init --instance {oldInstance}");
            BrainTestFixtures.InsertNode(oldDb, "learn:s1", "concept", "subject", "");
            BrainTestFixtures.InsertNode(oldDb, "learn:o1", "concept", "object", "");

            Environment.SetEnvironmentVariable("AITM_INSTANCE", oldInstance);
            string expected = (string)McpDll.Invoke("brain_learn", "triple", "learn:s1", "related", "learn:o1", "", "because text", false)!;

            string newDb = AitmCliRunner.InstanceDbPath(newInstance);
            AitmCliRunner.Run($"init --instance {newInstance}");
            BrainTestFixtures.InsertNode(newDb, "learn:s1", "concept", "subject", "");
            BrainTestFixtures.InsertNode(newDb, "learn:o1", "concept", "object", "");
            using SqliteConnection connection = StoreConnection.Open(newDb);
            string actual = new BrainLearnTool().ExecuteMcp(connection, "triple", "learn:s1", "related", "learn:o1", "", "because text");

            Assert.Equal(expected, actual);
            Assert.Equal("triple learned.", actual);

            using SqliteCommand tripleCount = connection.CreateCommand();
            tripleCount.CommandText = "SELECT count(*) FROM triple_now WHERE s='learn:s1' AND p='related' AND o='learn:o1'";
            Assert.Equal(1L, (long)tripleCount.ExecuteScalar()!);

            using SqliteCommand mutation = connection.CreateCommand();
            mutation.CommandText = "SELECT count(*) FROM mutations WHERE kind='triple' AND k='learn:s1 related learn:o1' AND why='brain_learn'";
            Assert.Equal(1L, (long)mutation.ExecuteScalar()!);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AITM_INSTANCE", previousInstanceEnv);
            AitmCliRunner.DeleteInstance(oldInstance);
            AitmCliRunner.DeleteInstance(newInstance);
        }
    }

    [Fact]
    public void McpShapeReinforcesAReassertedTripleInsteadOfDuplicating()
    {
        string oldInstance = AitmCliRunner.NewTestInstance("brain-learn-mcp-triple-reassert-old");
        string newInstance = AitmCliRunner.NewTestInstance("brain-learn-mcp-triple-reassert-new");
        string? previousInstanceEnv = Environment.GetEnvironmentVariable("AITM_INSTANCE");
        try
        {
            string oldDb = AitmCliRunner.InstanceDbPath(oldInstance);
            AitmCliRunner.Run($"init --instance {oldInstance}");
            BrainTestFixtures.InsertNode(oldDb, "learn:s2", "concept", "subject", "");
            BrainTestFixtures.InsertNode(oldDb, "learn:o2", "concept", "object", "");
            BrainTestFixtures.InsertSharingTriple(oldDb, "learn:s2", "learn:o2"); // seeds s2 -consumes-> o2

            Environment.SetEnvironmentVariable("AITM_INSTANCE", oldInstance);
            string expected = (string)McpDll.Invoke("brain_learn", "triple", "learn:s2", "consumes", "learn:o2", "", "", false)!;

            string newDb = AitmCliRunner.InstanceDbPath(newInstance);
            AitmCliRunner.Run($"init --instance {newInstance}");
            BrainTestFixtures.InsertNode(newDb, "learn:s2", "concept", "subject", "");
            BrainTestFixtures.InsertNode(newDb, "learn:o2", "concept", "object", "");
            BrainTestFixtures.InsertSharingTriple(newDb, "learn:s2", "learn:o2");
            using SqliteConnection connection = StoreConnection.Open(newDb);
            string actual = new BrainLearnTool().ExecuteMcp(connection, "triple", "learn:s2", "consumes", "learn:o2");

            Assert.Equal(expected, actual);
            Assert.Equal("reinforced (known relation, confidence bumped).", actual);

            using SqliteCommand tripleCount = connection.CreateCommand();
            tripleCount.CommandText = "SELECT count(*) FROM triple_now WHERE s='learn:s2' AND p='consumes' AND o='learn:o2'";
            Assert.Equal(1L, (long)tripleCount.ExecuteScalar()!);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AITM_INSTANCE", previousInstanceEnv);
            AitmCliRunner.DeleteInstance(oldInstance);
            AitmCliRunner.DeleteInstance(newInstance);
        }
    }

    [Fact]
    public void McpShapeLearnsANewSlotAndLogsTheMutation()
    {
        string oldInstance = AitmCliRunner.NewTestInstance("brain-learn-mcp-slot-new-old");
        string newInstance = AitmCliRunner.NewTestInstance("brain-learn-mcp-slot-new-new");
        string? previousInstanceEnv = Environment.GetEnvironmentVariable("AITM_INSTANCE");
        try
        {
            string oldDb = AitmCliRunner.InstanceDbPath(oldInstance);
            AitmCliRunner.Run($"init --instance {oldInstance}");
            BrainTestFixtures.InsertNode(oldDb, "learn:frame1", "codekind", "frame one", "");

            Environment.SetEnvironmentVariable("AITM_INSTANCE", oldInstance);
            string expected = (string)McpDll.Invoke("brain_learn", "slot", "learn:frame1", "place", "somewhere", "", "", false)!;

            string newDb = AitmCliRunner.InstanceDbPath(newInstance);
            AitmCliRunner.Run($"init --instance {newInstance}");
            BrainTestFixtures.InsertNode(newDb, "learn:frame1", "codekind", "frame one", "");
            using SqliteConnection connection = StoreConnection.Open(newDb);
            string actual = new BrainLearnTool().ExecuteMcp(connection, "slot", "learn:frame1", "place", "somewhere");

            Assert.Equal(expected, actual);
            Assert.Equal("slot learned.", actual);

            using SqliteCommand slotCount = connection.CreateCommand();
            slotCount.CommandText = "SELECT count(*) FROM slot_now WHERE frame_k='learn:frame1' AND name='place' AND value='somewhere'";
            Assert.Equal(1L, (long)slotCount.ExecuteScalar()!);

            using SqliteCommand mutation = connection.CreateCommand();
            mutation.CommandText = "SELECT count(*) FROM mutations WHERE kind='slot' AND why='brain_learn'";
            Assert.Equal(1L, (long)mutation.ExecuteScalar()!);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AITM_INSTANCE", previousInstanceEnv);
            AitmCliRunner.DeleteInstance(oldInstance);
            AitmCliRunner.DeleteInstance(newInstance);
        }
    }

    [Fact]
    public void McpShapeRejectsAnUnknownRowKind()
    {
        string instance = AitmCliRunner.NewTestInstance("brain-learn-mcp-bad-kind");
        string? previousInstanceEnv = Environment.GetEnvironmentVariable("AITM_INSTANCE");
        try
        {
            // Rejected, writes nothing, so sharing one instance is safe here.
            string dbPath = AitmCliRunner.InstanceDbPath(instance);
            AitmCliRunner.Run($"init --instance {instance}");

            Environment.SetEnvironmentVariable("AITM_INSTANCE", instance);
            string expected = (string)McpDll.Invoke("brain_learn", "bogus", "learn:x", "a", "b", "c", "", false)!;

            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = new BrainLearnTool().ExecuteMcp(connection, "bogus", "learn:x", "a", "b", "c");

            Assert.Equal(expected, actual);
            Assert.Equal("kind must be node | triple | slot.", actual);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AITM_INSTANCE", previousInstanceEnv);
            AitmCliRunner.DeleteInstance(instance);
        }
    }

    // --- CLI shape: ports the aitm.cs `selftest` checks that cover `brain learn` (aitm.cs:3078-3086,
    //     3204-3223), since the CLI verb already has selftest coverage today (RESTRUCTURE.md slice 17a note). ---

    [Fact]
    public void CliGuardRejectsAProseNodeKindAndWritesNothing()
    {
        string instance = AitmCliRunner.NewTestInstance("brain-learn-cli-guard-prose");
        try
        {
            AitmCliRunner.Run($"init --instance {instance}");
            string dbPath = AitmCliRunner.InstanceDbPath(instance);
            using SqliteConnection connection = StoreConnection.Open(dbPath);

            string result = new BrainLearnTool().ExecuteCli(connection, new[] { "node", "learn:scramx", "This sentence is not a kind token", "some label" });

            Assert.StartsWith("rejected:", result);
            using SqliteCommand count = connection.CreateCommand();
            count.CommandText = "SELECT count(*) FROM node_now WHERE k='learn:scramx'";
            Assert.Equal(0L, (long)count.ExecuteScalar()!);
        }
        finally
        {
            AitmCliRunner.DeleteInstance(instance);
        }
    }

    [Fact]
    public void CliGuardRejectsAParagraphLabelAndWritesNothing()
    {
        string instance = AitmCliRunner.NewTestInstance("brain-learn-cli-guard-paragraph");
        try
        {
            AitmCliRunner.Run($"init --instance {instance}");
            string dbPath = AitmCliRunner.InstanceDbPath(instance);
            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string paragraph = string.Join(' ', Enumerable.Repeat("word", 40));

            string result = new BrainLearnTool().ExecuteCli(connection, new[] { "node", "learn:scramy", "fact", paragraph });

            Assert.StartsWith("rejected:", result);
            using SqliteCommand count = connection.CreateCommand();
            count.CommandText = "SELECT count(*) FROM node_now WHERE k='learn:scramy'";
            Assert.Equal(0L, (long)count.ExecuteScalar()!);
        }
        finally
        {
            AitmCliRunner.DeleteInstance(instance);
        }
    }

    [Fact]
    public void CliWellFormedNodeWritesSilentlyAndLogsTheMutation()
    {
        string instance = AitmCliRunner.NewTestInstance("brain-learn-cli-ok");
        try
        {
            AitmCliRunner.Run($"init --instance {instance}");
            string dbPath = AitmCliRunner.InstanceDbPath(instance);
            using SqliteConnection connection = StoreConnection.Open(dbPath);

            string result = new BrainLearnTool().ExecuteCli(connection, new[] { "node", "learn:scramok", "fact", "short clean label" });

            Assert.Equal("", result);
            using SqliteCommand count = connection.CreateCommand();
            count.CommandText = "SELECT count(*) FROM node_now WHERE k='learn:scramok'";
            Assert.Equal(1L, (long)count.ExecuteScalar()!);

            using SqliteCommand mutation = connection.CreateCommand();
            mutation.CommandText = "SELECT count(*) FROM mutations WHERE kind='node' AND k='learn:scramok' AND why='brain learn'";
            Assert.Equal(1L, (long)mutation.ExecuteScalar()!);
        }
        finally
        {
            AitmCliRunner.DeleteInstance(instance);
        }
    }

    [Fact]
    public void CliReassertingATripleStrengthensConfidenceWithoutDuplicating()
    {
        string instance = AitmCliRunner.NewTestInstance("brain-learn-cli-triple-reassert");
        try
        {
            AitmCliRunner.Run($"init --instance {instance}");
            string dbPath = AitmCliRunner.InstanceDbPath(instance);
            BrainTestFixtures.InsertNode(dbPath, "learn:cs1", "project", "project one", "");
            BrainTestFixtures.InsertNode(dbPath, "learn:cs2", "project", "project two", "");
            using SqliteConnection connection = StoreConnection.Open(dbPath);
            BrainLearnTool tool = new();

            tool.ExecuteCli(connection, new[] { "triple", "learn:cs1", "related", "learn:cs2" });
            tool.ExecuteCli(connection, new[] { "triple", "learn:cs1", "related", "learn:cs2" });

            using SqliteCommand count = connection.CreateCommand();
            count.CommandText = "SELECT count(*) FROM triple_now WHERE s='learn:cs1' AND p='related' AND o='learn:cs2'";
            Assert.Equal(1L, (long)count.ExecuteScalar()!);

            using SqliteCommand confCount = connection.CreateCommand();
            confCount.CommandText = "SELECT count(*) FROM triple_now WHERE s='learn:cs1' AND p='related' AND o='learn:cs2' AND conf>1.0";
            Assert.Equal(1L, (long)confCount.ExecuteScalar()!);
        }
        finally
        {
            AitmCliRunner.DeleteInstance(instance);
        }
    }

    [Fact]
    public void CliLearnAutoResolvesACoveringGapButNotAnUnrelatedOne()
    {
        string instance = AitmCliRunner.NewTestInstance("brain-learn-cli-gap");
        try
        {
            AitmCliRunner.Run($"init --instance {instance}");
            string dbPath = AitmCliRunner.InstanceDbPath(instance);
            using SqliteConnection connection = StoreConnection.Open(dbPath);
            GapLog.Record(connection, "query", "quantumblockchain unicornrecipe zzz");

            new BrainLearnTool().ExecuteCli(connection, new[] { "node", "learn:gapfill", "fact", "quantumblockchain is the unicornrecipe zzz answer" });

            using SqliteCommand filled = connection.CreateCommand();
            filled.CommandText = "SELECT count(*) FROM gaps WHERE status='filled'";
            Assert.Equal(1L, (long)filled.ExecuteScalar()!);

            new BrainLearnTool().ExecuteCli(connection, new[] { "node", "learn:unrelated", "fact", "totally unrelated text" });

            using SqliteCommand stillFilled = connection.CreateCommand();
            stillFilled.CommandText = "SELECT count(*) FROM gaps WHERE status='filled'";
            Assert.Equal(1L, (long)stillFilled.ExecuteScalar()!);
        }
        finally
        {
            AitmCliRunner.DeleteInstance(instance);
        }
    }
}
