using Grimoira.Store.Data;
using System.Globalization;
using System.Text.RegularExpressions;
using Grimoira.TestSupport;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Grimoira.Layout.Tests;

// RESTRUCTURE.md slice 24, CLI lane part 4b: proves the 9 Grimoira.Brain write verbs (learn, learn-batch,
// set-hard, merge, forget, unlink, shed-node, tidy, distill) behave exactly as they did before the
// wiring, by running each one on a fresh temp instance against the frozen "before" binary
// (OldVsNewCli.OracleDll, built from the commit slice 24 started at) and against today's
// bin-cli/grimoira.dll, and diffing stdout, stderr and exit code. Every wired verb gets at least a
// no-arg/usage or normal run, most also get a deliberate error case, and the verbs that take free text
// (a node key, label, or subject/object) get a hyphen, slash, dot and underscore variant on top of that.
//
// merge/forget/unlink/tidy/distill/shed-node are delete/bulk-change verbs (RESTRUCTURE.md section 5,
// design checklist): their tool classes take a VACUUM INTO backup before writing (BackupTool.Execute),
// which the old inline grimoira.cs code never did. That backup writes a file under root/backups/ but never
// prints to stdout (BackupTool's return value is discarded), so it does not break old-vs-new stdout
// parity — proven here by also reading the resulting node/triple/mutations rows back from both temp
// stores and comparing them, not just stdout.
//
// Every fixture used here is a fake key/label/predicate under a throwaway temp instance — never a real
// repo or ~/.claude, and never real content.
public partial class BrainWriteCliParityTests
{
    private static string StripVolatile(string s) =>
        IsoTimestamp().Replace(ParenthesisedTimingMs().Replace(s, "(<ms>)"), "<ts>");

    private static (string stdout, string stderr, int exitCode) RunNormalized(
        OldVsNewCli.Result result, string instance, string dbPath)
    {
        string NormalizeOne(string s) => StripVolatile(s.Replace(instance, "<instance>").Replace(dbPath, "<db>"));
        return (NormalizeOne(result.Stdout), NormalizeOne(result.Stderr), result.ExitCode);
    }

    // Runs `setup` then `command` on both binaries (each on its own fresh instance) and returns the two
    // instances so a caller can also diff table rows, in addition to the stdout/stderr/exit-code check
    // this always does.
    private static (string oldInstance, string newInstance) RunParity(string[] setup, string command)
    {
        string oldInstance = GrimoiraCliRunner.NewTestInstance("p4bo");
        string newInstance = GrimoiraCliRunner.NewTestInstance("p4bn");
        string oldDll = OldVsNewCli.OracleDll();
        string newDll = OldVsNewCli.BinCliDll();
        foreach (string s in setup)
        {
            OldVsNewCli.Run(oldDll, oldInstance, s);
            OldVsNewCli.Run(newDll, newInstance, s);
        }

        OldVsNewCli.Result oldResult = OldVsNewCli.Run(oldDll, oldInstance, command);
        OldVsNewCli.Result newResult = OldVsNewCli.Run(newDll, newInstance, command);

        (string oStdout, string oStderr, int oExit) =
            RunNormalized(oldResult, oldInstance, GrimoiraCliRunner.InstanceDbPath(oldInstance));
        (string nStdout, string nStderr, int nExit) =
            RunNormalized(newResult, newInstance, GrimoiraCliRunner.InstanceDbPath(newInstance));

        Assert.Equal(oStdout, nStdout);
        Assert.Equal(oStderr, nStderr);
        Assert.Equal(oExit, nExit);

        return (oldInstance, newInstance);
    }

    private static void AssertParity(string[] setup, string command)
    {
        (string oldInstance, string newInstance) = RunParity(setup, command);
        GrimoiraCliRunner.DeleteInstance(oldInstance);
        GrimoiraCliRunner.DeleteInstance(newInstance);
    }

    // Same as AssertParity, but also reads `sql` back from both resulting stores and asserts the rows
    // are identical — the check the write verbs (learn, set-hard, merge, forget, unlink, shed-node,
    // tidy, distill, learn-batch) need beyond stdout, since a wiring bug could still print the same
    // message while writing the wrong row.
    private static void AssertParityAndRows(string[] setup, string command, params string[] sqls)
    {
        (string oldInstance, string newInstance) = RunParity(setup, command);
        try
        {
            foreach (string sql in sqls)
            {
                List<string> oldRows = SelectRows(GrimoiraCliRunner.InstanceDbPath(oldInstance), sql);
                List<string> newRows = SelectRows(GrimoiraCliRunner.InstanceDbPath(newInstance), sql);
                Assert.Equal(oldRows, newRows);
            }
        }
        finally
        {
            GrimoiraCliRunner.DeleteInstance(oldInstance);
            GrimoiraCliRunner.DeleteInstance(newInstance);
        }
    }

    private static List<string> SelectRows(string dbPath, string sql)
    {
        List<string> rows = [];
        using SqliteConnection connection = new($"Data Source={dbPath};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            List<string> cols = [];
            for (int i = 0; i < reader.FieldCount; i++)
                cols.Add(reader.IsDBNull(i) ? "\x1f" : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture) ?? "");
            rows.Add(string.Join("|", cols));
        }
        rows.Sort(StringComparer.Ordinal);
        return rows;
    }

    private const string NodeRowsSql = "SELECT k,kind,label,gloss,COALESCE(scheme,''),hard FROM node_now ORDER BY k";
    private const string TripleRowsSql = "SELECT s,p,o,COALESCE(because,''),hard FROM triple_now ORDER BY s,p,o";
    private const string MutationRowsSql = "SELECT op,kind,k,COALESCE(before,'\x1f'),COALESCE(after,'\x1f'),why FROM mutations ORDER BY id";

    public static IEnumerable<object[]> Scenarios()
    {
        (string name, string[] setup, string command)[] cases =
        [
            // learn — bare/usage shapes, an unknown sub-verb, and a guard rejection.
            ("learn-bare", ["init"], "brain learn"),
            ("learn-node-missing-args", ["init"], "brain learn node onlyonearg"),
            ("learn-triple-missing-args", ["init"], "brain learn triple only two"),
            ("learn-slot-missing-args", ["init"], "brain learn slot only two"),
            ("learn-unknown-subverb", ["init"], "brain learn bogus"),
            ("learn-node-bad-kind-guard", ["init"], "brain learn node p4b:guard \"This is prose\" a label"),

            // set-hard — usage and a miss.
            ("set-hard-usage", ["init"], "brain set-hard onlyone"),
            ("set-hard-miss", ["init"], "brain set-hard p4b:never-existed 1"),

            // merge — usage, same key, and a miss (neither side is a live node).
            ("merge-usage", ["init"], "brain merge onlyone"),
            ("merge-same", ["init"], "brain merge p4b:samekey p4b:samekey"),
            ("merge-miss", ["init"], "brain merge p4b:missing-src p4b:missing-dst"),

            // forget — usage and a miss.
            ("forget-usage", ["init"], "brain forget"),
            ("forget-miss", ["init"], "brain forget p4b:never-existed"),

            // unlink — usage and a miss.
            ("unlink-usage", ["init"], "brain unlink subject predicate"),
            ("unlink-miss", ["init"], "brain unlink p4b:s related p4b:o"),

            // shed-node — missing --key (ArgumentException) and a miss (no such live node — still
            // succeeds, since the UPDATE just affects zero rows).
            ("shed-node-missing-flag", ["init"], "shed-node"),
            ("shed-node-miss", ["init"], "shed-node --key p4b:never-existed"),

            // tidy and distill — bare runs on an empty store; the only shape with nothing set up first.
            ("tidy-empty", ["init"], "brain tidy"),
            ("distill-empty", ["init"], "brain distill"),

            // learn-batch — missing --from and a file that does not exist.
            ("learn-batch-missing-flag", ["init"], "brain learn-batch"),
            ("learn-batch-missing-file", ["init"], "brain learn-batch --from \"/no/such/directory/grimoira-slice24-fixture.txt\""),
        ];
        foreach ((string name, string[] setup, string command) in cases)
            yield return [name, setup, command];
    }

    [Theory]
    [MemberData(nameof(Scenarios))]
    public void OldAndNewCliAgree(string _, string[] setup, string command) => AssertParity(setup, command);

    // brain learn node/triple/slot on a real store, including the punctuated-key shapes (a hyphen, slash,
    // dot and underscore each get their own node key) — a tokenizer or guard difference on the CLI side
    // would show up as a divergent node_now/mutations row between old and new.
    [Fact]
    public void LearnNodeTripleAndSlotMatchOldBehaviourAndRows()
    {
        AssertParityAndRows(["init"],
            "brain learn node p4b:node-key fact \"a short label\" --gloss \"a gloss\" --scheme test",
            NodeRowsSql, MutationRowsSql);

        foreach (string key in new[] { "p4b:hook-config", "p4b:hook/config", "p4b:hook.config", "p4b:hook_config" })
            AssertParityAndRows(["init"], $"brain learn node {key} fact \"a short label\"", NodeRowsSql, MutationRowsSql);

        // 'related' is a link predicate (pred_vocab.is_link=1): the subject and object must both be
        // live nodes already, or the dangling-subject/-object trigger rejects the insert.
        string[] tripleSetup =
        [
            "init",
            "brain learn node p4b:subject fact \"subject label\"",
            "brain learn node p4b:object fact \"object label\"",
        ];
        AssertParityAndRows(tripleSetup,
            "brain learn triple p4b:subject related p4b:object --because \"a fixture reason\"",
            TripleRowsSql, MutationRowsSql);

        foreach (string subject in new[] { "p4b:hook-config", "p4b:hook/config", "p4b:hook.config", "p4b:hook_config" })
        {
            string[] setup =
            [
                "init",
                $"brain learn node {subject} fact \"subject label\"",
                "brain learn node p4b:object fact \"object label\"",
            ];
            AssertParityAndRows(setup, $"brain learn triple {subject} related p4b:object", TripleRowsSql, MutationRowsSql);
        }

        // The slot frame is also checked for a live node (dangling-slot-frame trigger).
        string[] slotSetup = ["init", "brain learn node p4b:frame fact \"frame label\""];
        AssertParityAndRows(slotSetup, "brain learn slot p4b:frame name value --facet text", MutationRowsSql);
    }

    [Fact]
    public void SetHardMatchesOldBehaviourOnARealNode()
    {
        string[] setup = ["init", "brain learn node p4b:hard-target fact \"a short label\""];
        AssertParityAndRows(setup, "brain set-hard p4b:hard-target 1", NodeRowsSql, MutationRowsSql);
    }

    [Fact]
    public void MergeMatchesOldBehaviourOnRealNodes()
    {
        string[] setup =
        [
            "init",
            "brain learn node p4b:merge-src fact \"source label\"",
            "brain learn node p4b:merge-dst fact \"target label\"",
            "brain learn node p4b:other fact \"other label\"",
            "brain learn triple p4b:merge-src related p4b:other",
        ];
        AssertParityAndRows(setup, "brain merge p4b:merge-src p4b:merge-dst",
            NodeRowsSql, TripleRowsSql, MutationRowsSql);
    }

    [Fact]
    public void ForgetMatchesOldBehaviourOnARealNode()
    {
        string[] setup =
        [
            "init",
            "brain learn node p4b:forget-target fact \"a short label\"",
            "brain learn node p4b:other fact \"other label\"",
            "brain learn triple p4b:forget-target related p4b:other",
        ];
        AssertParityAndRows(setup, "brain forget p4b:forget-target", NodeRowsSql, TripleRowsSql, MutationRowsSql);
    }

    [Fact]
    public void UnlinkMatchesOldBehaviourOnARealTriple()
    {
        string[] setup =
        [
            "init",
            "brain learn node p4b:unlink-s fact \"subject label\"",
            "brain learn node p4b:unlink-o fact \"object label\"",
            "brain learn triple p4b:unlink-s related p4b:unlink-o",
        ];
        AssertParityAndRows(setup, "brain unlink p4b:unlink-s related p4b:unlink-o", TripleRowsSql, MutationRowsSql);
    }

    [Fact]
    public void ShedNodeMatchesOldBehaviourOnARealNode()
    {
        string[] setup =
        [
            "init",
            "brain learn node p4b:shed-target fact \"a short label\"",
            "brain learn node p4b:other fact \"other label\"",
            "brain learn triple p4b:shed-target related p4b:other",
        ];
        AssertParityAndRows(setup, "shed-node --key p4b:shed-target", NodeRowsSql, TripleRowsSql);
    }

    [Fact]
    public void TidyMatchesOldBehaviourOnARealNode()
    {
        // No --scheme given, so AddNode leaves scheme empty — the exact gap tidy backfills.
        string[] setup = ["init", "brain learn node p4b:tidy-target fact \"a short label\""];
        AssertParityAndRows(setup, "brain tidy", NodeRowsSql);
    }

    [Fact]
    public void DistillMatchesOldBehaviourOnRealChannels()
    {
        string[] setup =
        [
            "init",
            "add --term p4b-distill-term --value \"a fixture fact value\" --category manual",
        ];
        AssertParityAndRows(setup, "brain distill", NodeRowsSql, MutationRowsSql);
    }

    // learn-batch needs a real pipe-delimited fixture file. Nodes come before the triples/slots that
    // reference them, as the file format itself requires.
    [Fact]
    public void LearnBatchMatchesOldBehaviourOnARealFixture()
    {
        string dir = MakeTempDir("grimoira-slice24-learnbatch-fixture");
        string file = Path.Combine(dir, "batch.txt");
        File.WriteAllText(file, string.Join('\n',
            "node | p4b:batch-a | fact | a batch label | a batch gloss | 0 | test",
            "node | p4b:batch-b | fact | another batch label | | 0 |",
            "triple | p4b:batch-a | related | p4b:batch-b | a batch reason",
            "slot | p4b:batch-a | name | value | text | 0",
            "# a comment line",
            "malformed line with no pipes") + "\n");
        try
        {
            AssertParityAndRows(["init"], $"brain learn-batch --from \"{file}\"",
                NodeRowsSql, TripleRowsSql, MutationRowsSql);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static string MakeTempDir(string label)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"{label}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    [GeneratedRegex(@"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d+Z", RegexOptions.None, RegexTimeout.Milliseconds)]
    private static partial Regex IsoTimestamp();

    [GeneratedRegex(@"\(\d+[.,]\d+ms\)", RegexOptions.None, RegexTimeout.Milliseconds)]
    private static partial Regex ParenthesisedTimingMs();
}
