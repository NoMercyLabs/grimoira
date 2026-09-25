using Aitm.Brain.Tools;
using Aitm.Store.Data;
using Aitm.TestSupport;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Aitm.Brain.Tests;

public class BrainStageToolTests
{
    // --- CLI shape: oracle is today's aitm.cs StageCmd (aitm.cs:1913). None of this has a test today
    //     (RESTRUCTURE.md slice 18 note), so every case here is pinned against the running old binary. ---

    [Fact]
    public void CliStageNodeAppendsTheLedgerLineTodaysBinaryWrites()
    {
        string oldInstance = AitmCliRunner.NewTestInstance("stage-cli-node-old");
        string newInstance = AitmCliRunner.NewTestInstance("stage-cli-node-new");
        try
        {
            AitmCliRunner.Run($"init --instance {oldInstance}");
            (string oldOut, int oldExit) = AitmCliRunner.Run($"stage node stage:n1 fact \"a widget\" --gloss \"widget gloss\" --scheme scheme1 --hard --instance {oldInstance}");
            Assert.Equal(0, oldExit);
            string oldLedger = File.ReadAllText(Path.Combine(AitmCliRunner.InstanceDir(oldInstance), "pending-learn.jsonl"));

            AitmCliRunner.Run($"init --instance {newInstance}");
            string newDb = AitmCliRunner.InstanceDbPath(newInstance);
            using SqliteConnection connection = StoreConnection.Open(newDb);
            string actual = new BrainStageTool().ExecuteCli(connection, ["node", "stage:n1", "fact", "a widget"], gloss: "widget gloss", scheme: "scheme1", hard: true);

            Assert.Equal(oldOut.Trim(), actual);
            Assert.Equal("staged node stage:n1 (owe flush).", actual);

            string newLedger = File.ReadAllText(BrainStageTool.LedgerPath(connection));
            Assert.Equal(oldLedger, newLedger);
        }
        finally
        {
            AitmCliRunner.DeleteInstance(oldInstance);
            AitmCliRunner.DeleteInstance(newInstance);
        }
    }

    [Fact]
    public void CliStageTripleAppendsTheLedgerLineTodaysBinaryWrites()
    {
        string oldInstance = AitmCliRunner.NewTestInstance("stage-cli-triple-old");
        string newInstance = AitmCliRunner.NewTestInstance("stage-cli-triple-new");
        try
        {
            AitmCliRunner.Run($"init --instance {oldInstance}");
            (string oldOut, int oldExit) = AitmCliRunner.Run($"stage triple stage:s1 related stage:o1 --because \"because text\" --instance {oldInstance}");
            Assert.Equal(0, oldExit);
            string oldLedger = File.ReadAllText(Path.Combine(AitmCliRunner.InstanceDir(oldInstance), "pending-learn.jsonl"));

            AitmCliRunner.Run($"init --instance {newInstance}");
            string newDb = AitmCliRunner.InstanceDbPath(newInstance);
            using SqliteConnection connection = StoreConnection.Open(newDb);
            string actual = new BrainStageTool().ExecuteCli(connection, ["triple", "stage:s1", "related", "stage:o1"], because: "because text");

            Assert.Equal(oldOut.Trim(), actual);
            Assert.Equal("staged triple stage:s1 related stage:o1 (owe flush).", actual);

            string newLedger = File.ReadAllText(BrainStageTool.LedgerPath(connection));
            Assert.Equal(oldLedger, newLedger);
        }
        finally
        {
            AitmCliRunner.DeleteInstance(oldInstance);
            AitmCliRunner.DeleteInstance(newInstance);
        }
    }

    [Fact]
    public void CliStageSlotAppendsTheLedgerLineTodaysBinaryWrites()
    {
        string oldInstance = AitmCliRunner.NewTestInstance("stage-cli-slot-old");
        string newInstance = AitmCliRunner.NewTestInstance("stage-cli-slot-new");
        try
        {
            AitmCliRunner.Run($"init --instance {oldInstance}");
            (string oldOut, int oldExit) = AitmCliRunner.Run($"stage slot stage:frame1 place somewhere --facet text --multi --instance {oldInstance}");
            Assert.Equal(0, oldExit);
            string oldLedger = File.ReadAllText(Path.Combine(AitmCliRunner.InstanceDir(oldInstance), "pending-learn.jsonl"));

            AitmCliRunner.Run($"init --instance {newInstance}");
            string newDb = AitmCliRunner.InstanceDbPath(newInstance);
            using SqliteConnection connection = StoreConnection.Open(newDb);
            string actual = new BrainStageTool().ExecuteCli(connection, ["slot", "stage:frame1", "place", "somewhere"], facet: "text", multi: true);

            Assert.Equal(oldOut.Trim(), actual);
            Assert.Equal("staged slot stage:frame1.place (owe flush).", actual);

            string newLedger = File.ReadAllText(BrainStageTool.LedgerPath(connection));
            Assert.Equal(oldLedger, newLedger);
        }
        finally
        {
            AitmCliRunner.DeleteInstance(oldInstance);
            AitmCliRunner.DeleteInstance(newInstance);
        }
    }

    [Fact]
    public void CliStageListReportsNothingStagedOnAFreshInstance()
    {
        string oldInstance = AitmCliRunner.NewTestInstance("stage-cli-list-empty-old");
        string newInstance = AitmCliRunner.NewTestInstance("stage-cli-list-empty-new");
        try
        {
            AitmCliRunner.Run($"init --instance {oldInstance}");
            (string oldOut, int oldExit) = AitmCliRunner.Run($"stage list --instance {oldInstance}");
            Assert.Equal(0, oldExit);

            AitmCliRunner.Run($"init --instance {newInstance}");
            using SqliteConnection connection = StoreConnection.Open(AitmCliRunner.InstanceDbPath(newInstance));
            string actual = new BrainStageTool().ExecuteCli(connection, ["list"]);

            Assert.Equal(oldOut.Trim(), actual);
            Assert.Equal("nothing staged.", actual);
        }
        finally
        {
            AitmCliRunner.DeleteInstance(oldInstance);
            AitmCliRunner.DeleteInstance(newInstance);
        }
    }

    [Fact]
    public void CliStageListShowsWhatWasStaged()
    {
        string instance = AitmCliRunner.NewTestInstance("stage-cli-list");
        try
        {
            AitmCliRunner.Run($"init --instance {instance}");
            using SqliteConnection connection = StoreConnection.Open(AitmCliRunner.InstanceDbPath(instance));
            BrainStageTool tool = new();
            tool.ExecuteCli(connection, ["node", "stage:listme", "fact", "a widget"]);

            string listed = tool.ExecuteCli(connection, ["list"]);

            Assert.Contains("stage:listme", listed);
        }
        finally
        {
            AitmCliRunner.DeleteInstance(instance);
        }
    }

    [Theory]
    [InlineData("clear")]
    [InlineData("dismiss")]
    public void CliStageClearAndDismissDeleteTheLedgerWithoutPersisting(string sub)
    {
        string oldInstance = AitmCliRunner.NewTestInstance($"stage-cli-{sub}-old");
        string newInstance = AitmCliRunner.NewTestInstance($"stage-cli-{sub}-new");
        try
        {
            AitmCliRunner.Run($"init --instance {oldInstance}");
            AitmCliRunner.Run($"stage node stage:clearme fact \"a widget\" --instance {oldInstance}");
            (string oldOut, int oldExit) = AitmCliRunner.Run($"stage {sub} --instance {oldInstance}");
            Assert.Equal(0, oldExit);

            AitmCliRunner.Run($"init --instance {newInstance}");
            using SqliteConnection connection = StoreConnection.Open(AitmCliRunner.InstanceDbPath(newInstance));
            BrainStageTool tool = new();
            tool.ExecuteCli(connection, ["node", "stage:clearme", "fact", "a widget"]);
            string actual = tool.ExecuteCli(connection, [sub]);

            Assert.Equal(oldOut.Trim(), actual);
            Assert.Equal("staged learnings cleared (nothing persisted).", actual);
            Assert.False(File.Exists(BrainStageTool.LedgerPath(connection)));
        }
        finally
        {
            AitmCliRunner.DeleteInstance(oldInstance);
            AitmCliRunner.DeleteInstance(newInstance);
        }
    }

    [Fact]
    public void CliStageGuardRejectsAProseNodeKindAndWritesNothing()
    {
        // Not compared against a fresh CLI-process oracle run here: the redirected-pipe round trip
        // mangles this message's em dash (BrainLearnToolTests' own CLI guard tests hit the same thing
        // and skip the process oracle for the same reason). The guard text itself is still pinned,
        // verbatim, against NodeGuard.ValidateCli (Data/NodeGuard.cs, copied from aitm.cs:418-424).
        string instance = AitmCliRunner.NewTestInstance("stage-cli-guard");
        try
        {
            AitmCliRunner.Run($"init --instance {instance}");
            using SqliteConnection connection = StoreConnection.Open(AitmCliRunner.InstanceDbPath(instance));
            string actual = new BrainStageTool().ExecuteCli(connection, ["node", "stage:bad", "This is prose", "some label"]);

            Assert.StartsWith("rejected:", actual);
            Assert.Contains("Yours looks like prose", actual);
            Assert.False(File.Exists(BrainStageTool.LedgerPath(connection)));
        }
        finally
        {
            AitmCliRunner.DeleteInstance(instance);
        }
    }

    // --- MCP shape: pinned against today's bin/mcp.dll AitmTools.brain_stage (mcp.cs:924). Ports
    //     mcp-stage.test.mjs (RESTRUCTURE.md slice 18: "mcp-stage.test.mjs is ported"). ---

    [Fact]
    public void McpShapeCoercesANodeKindPassedAsTheRowTypeAndStagesANodeRow()
    {
        // mcp-stage.test.mjs's exact scenario: brain_stage rejected kind="rule" on every call because
        // "kind" names the ROW TYPE and the node's OWN kind goes in "a" — two things called kind. Row
        // types and node kinds are disjoint, so "rule" can only ever have meant a node of kind rule.
        string oldInstance = AitmCliRunner.NewTestInstance("stage-mcp-coerce-old");
        string newInstance = AitmCliRunner.NewTestInstance("stage-mcp-coerce-new");
        string? previousInstanceEnv = Environment.GetEnvironmentVariable("AITM_INSTANCE");
        try
        {
            AitmCliRunner.Run($"init --instance {oldInstance}");
            Environment.SetEnvironmentVariable("AITM_INSTANCE", oldInstance);
            string expected = (string)McpDll.Invoke("brain_stage", "rule", "stage-coercion-probe", "a probe label", "the full statement", "", "", false)!;
            string oldLedger = File.ReadAllText(Path.Combine(AitmCliRunner.InstanceDir(oldInstance), "pending-learn.jsonl"));

            AitmCliRunner.Run($"init --instance {newInstance}");
            using SqliteConnection connection = StoreConnection.Open(AitmCliRunner.InstanceDbPath(newInstance));
            string actual = new BrainStageTool().ExecuteMcp(connection, "rule", "stage-coercion-probe", "a probe label", "the full statement");

            Assert.Equal(expected, actual);
            Assert.DoesNotMatch("rejected:", actual);
            Assert.Contains("ROW type", actual);

            string newLedger = File.ReadAllText(BrainStageTool.LedgerPath(connection));
            Assert.Equal(oldLedger, newLedger);
            Assert.Contains("\"k\":\"node\"", newLedger);
            Assert.Contains("\"kind\":\"rule\"", newLedger);
            Assert.Contains("\"label\":\"a probe label\"", newLedger);
            Assert.Contains("\"gloss\":\"the full statement\"", newLedger);
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
        string instance = AitmCliRunner.NewTestInstance("stage-mcp-guard");
        string? previousInstanceEnv = Environment.GetEnvironmentVariable("AITM_INSTANCE");
        try
        {
            string dbPath = AitmCliRunner.InstanceDbPath(instance);
            AitmCliRunner.Run($"init --instance {instance}");
            Environment.SetEnvironmentVariable("AITM_INSTANCE", instance);
            string expected = (string)McpDll.Invoke("brain_stage", "node", "stage:bad", "This is prose", "some label", "", "", false)!;

            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = new BrainStageTool().ExecuteMcp(connection, "node", "stage:bad", "This is prose", "some label");

            Assert.Equal(expected, actual);
            Assert.StartsWith("rejected:", actual);
            Assert.Contains("is not a node kind", actual);
            Assert.False(File.Exists(BrainStageTool.LedgerPath(connection)));
        }
        finally
        {
            Environment.SetEnvironmentVariable("AITM_INSTANCE", previousInstanceEnv);
            AitmCliRunner.DeleteInstance(instance);
        }
    }

    [Fact]
    public void McpShapeStagesATripleRow()
    {
        string oldInstance = AitmCliRunner.NewTestInstance("stage-mcp-triple-old");
        string newInstance = AitmCliRunner.NewTestInstance("stage-mcp-triple-new");
        string? previousInstanceEnv = Environment.GetEnvironmentVariable("AITM_INSTANCE");
        try
        {
            AitmCliRunner.Run($"init --instance {oldInstance}");
            Environment.SetEnvironmentVariable("AITM_INSTANCE", oldInstance);
            string expected = (string)McpDll.Invoke("brain_stage", "triple", "stage:s2", "related", "stage:o2", "", "because text", false)!;
            string oldLedger = File.ReadAllText(Path.Combine(AitmCliRunner.InstanceDir(oldInstance), "pending-learn.jsonl"));

            AitmCliRunner.Run($"init --instance {newInstance}");
            using SqliteConnection connection = StoreConnection.Open(AitmCliRunner.InstanceDbPath(newInstance));
            string actual = new BrainStageTool().ExecuteMcp(connection, "triple", "stage:s2", "related", "stage:o2", "", "because text");

            Assert.Equal(expected, actual);
            Assert.Equal("staged triple stage:s2 (owe brain_flush).", actual);

            string newLedger = File.ReadAllText(BrainStageTool.LedgerPath(connection));
            Assert.Equal(oldLedger, newLedger);
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
        string instance = AitmCliRunner.NewTestInstance("stage-mcp-bad-kind");
        string? previousInstanceEnv = Environment.GetEnvironmentVariable("AITM_INSTANCE");
        try
        {
            string dbPath = AitmCliRunner.InstanceDbPath(instance);
            AitmCliRunner.Run($"init --instance {instance}");
            Environment.SetEnvironmentVariable("AITM_INSTANCE", instance);
            string expected = (string)McpDll.Invoke("brain_stage", "bogus", "stage:x", "a", "b", "c", "", false)!;

            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = new BrainStageTool().ExecuteMcp(connection, "bogus", "stage:x", "a", "b", "c");

            Assert.Equal(expected, actual);
            Assert.StartsWith("rejected:", actual);
            Assert.Contains("is not a row type", actual);
            Assert.False(File.Exists(BrainStageTool.LedgerPath(connection)));
        }
        finally
        {
            Environment.SetEnvironmentVariable("AITM_INSTANCE", previousInstanceEnv);
            AitmCliRunner.DeleteInstance(instance);
        }
    }
}
