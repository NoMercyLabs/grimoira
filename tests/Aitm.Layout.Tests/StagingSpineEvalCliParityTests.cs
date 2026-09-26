using Aitm.Store.Data;
using System.Globalization;
using System.Text.RegularExpressions;
using Aitm.TestSupport;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Aitm.Layout.Tests;

// RESTRUCTURE.md slice 24, CLI lane part 4c (the last part of the lane): proves the last 6 golden CLI
// verbs (stage, flush, seed, spine-export, spine-import, eval) behave exactly as they did before the
// wiring, by running each one on a fresh temp instance against the frozen "before" binary
// (OldVsNewCli.OracleDll, built from the commit slice 24 started at) and against today's
// bin-cli/aitm.dll, and diffing stdout, stderr and exit code. `stage` and `flush` also write to a file
// (pending-learn.jsonl) beside the store, so those scenarios compare that file byte-for-byte between the
// two runs, on top of the resulting rows for the ones that actually persist (flush, seed, spine-import).
// `spine-export` writes a whole file rather than a DB row, so its scenario compares the exported file
// byte-for-byte instead.
//
// Every fixture used here is a fake key/label/predicate under a throwaway temp instance — never a real
// repo or ~/.claude, and never real content.
public partial class StagingSpineEvalCliParityTests
{
    // eval prints its own elapsed time per question via "{ms,5:F2}ms" — a 5-char right-aligned field, so
    // a single-digit value ("1.23ms") carries one more leading pad space than a double-digit one
    // ("12.34ms"). Leaving that pad space out of the match made the old-vs-new diff flaky under load: two
    // runs landing on either side of the single/double-digit boundary differed by exactly one space
    // ahead of an otherwise-identical line. Consuming the leading whitespace along with the number fixes
    // it. On top of the "(N.Nms)" shape other verbs use, so both need normalizing before a diff.
    private static string StripVolatile(string s) =>
        PaddedTimingMs().Replace(IsoTimestamp().Replace(ParenthesisedTimingMs().Replace(s, "(<ms>)"), "<ts>"), "<ms>");

    private static (string stdout, string stderr, int exitCode) RunNormalized(
        OldVsNewCli.Result result, string instance, string dbPath)
    {
        string NormalizeOne(string s) => StripVolatile(s.Replace(instance, "<instance>").Replace(dbPath, "<db>"));
        return (NormalizeOne(result.Stdout), NormalizeOne(result.Stderr), result.ExitCode);
    }

    // Runs `setup` then `command` on both binaries (each on its own fresh instance) and returns the two
    // instances so a caller can also diff table rows or sibling files, in addition to the
    // stdout/stderr/exit-code check this always does.
    private static (string oldInstance, string newInstance) RunParity(string[] setup, string command)
    {
        string oldInstance = AitmCliRunner.NewTestInstance("p4co");
        string newInstance = AitmCliRunner.NewTestInstance("p4cn");
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
            RunNormalized(oldResult, oldInstance, AitmCliRunner.InstanceDbPath(oldInstance));
        (string nStdout, string nStderr, int nExit) =
            RunNormalized(newResult, newInstance, AitmCliRunner.InstanceDbPath(newInstance));

        Assert.Equal(oStdout, nStdout);
        Assert.Equal(oStderr, nStderr);
        Assert.Equal(oExit, nExit);

        return (oldInstance, newInstance);
    }

    private static void AssertParity(string[] setup, string command)
    {
        (string oldInstance, string newInstance) = RunParity(setup, command);
        AitmCliRunner.DeleteInstance(oldInstance);
        AitmCliRunner.DeleteInstance(newInstance);
    }

    private static string LedgerPath(string instance) =>
        Path.Combine(AitmCliRunner.InstanceDir(instance), "pending-learn.jsonl");

    private static string ReadLedgerOrEmpty(string instance)
    {
        string path = LedgerPath(instance);
        return File.Exists(path) ? File.ReadAllText(path) : "";
    }

    // Same as AssertParity, but also asserts the pending-learn.jsonl ledger ends up byte-identical on
    // both sides — the check `stage` needs beyond stdout, since a wiring bug could still print the same
    // message while appending a different (or no) ledger line.
    private static void AssertParityAndLedger(string[] setup, string command)
    {
        (string oldInstance, string newInstance) = RunParity(setup, command);
        try
        {
            Assert.Equal(ReadLedgerOrEmpty(oldInstance), ReadLedgerOrEmpty(newInstance));
        }
        finally
        {
            AitmCliRunner.DeleteInstance(oldInstance);
            AitmCliRunner.DeleteInstance(newInstance);
        }
    }

    private static void AssertParityAndRows(string[] setup, string command, params string[] sqls)
    {
        (string oldInstance, string newInstance) = RunParity(setup, command);
        try
        {
            foreach (string sql in sqls)
            {
                List<string> oldRows = SelectRows(AitmCliRunner.InstanceDbPath(oldInstance), sql);
                List<string> newRows = SelectRows(AitmCliRunner.InstanceDbPath(newInstance), sql);
                Assert.Equal(oldRows, newRows);
            }
        }
        finally
        {
            AitmCliRunner.DeleteInstance(oldInstance);
            AitmCliRunner.DeleteInstance(newInstance);
        }
    }

    private static List<string> SelectRows(string dbPath, string sql)
    {
        List<string> rows = [];
        using SqliteConnection connection = new($"Data Source={dbPath};Mode=ReadOnly");
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
    private const string SlotRowsSql = "SELECT frame_k,name,value,COALESCE(facet,''),multi FROM slot_now ORDER BY frame_k,name,value";
    private const string MutationRowsSql = "SELECT op,kind,k,COALESCE(before,'\x1f'),COALESCE(after,'\x1f'),why FROM mutations ORDER BY id";
    private const string AliasRowsSql = "SELECT short,k FROM proj_alias ORDER BY short";
    private const string TermRowsSql = "SELECT term,canonical FROM term_alias ORDER BY term,canonical";
    private const string EdgeRowsSql = "SELECT symbol,COALESCE(contract,''),COALESCE(project,''),file,line,COALESCE(usage,''),COALESCE(hardcoded,0) FROM edges ORDER BY symbol,file";

    public static IEnumerable<object[]> Scenarios()
    {
        (string name, string[] setup, string command)[] cases =
        [
            // stage — bare/usage shapes, an unknown sub-verb, a guard rejection, and list/clear/dismiss
            // on an empty ledger.
            ("stage-list-empty", ["init"], "stage list"),
            ("stage-node-missing-args", ["init"], "stage node onlyonearg"),
            ("stage-triple-missing-args", ["init"], "stage triple only two"),
            ("stage-slot-missing-args", ["init"], "stage slot only two"),
            ("stage-unknown-subverb", ["init"], "stage bogus"),
            ("stage-clear-empty", ["init"], "stage clear"),
            ("stage-dismiss-empty", ["init"], "stage dismiss"),
            ("stage-node-bad-kind-guard", ["init"], "stage node p4c:guard \"This is prose\" a label"),

            // flush — nothing staged.
            ("flush-nothing-staged", ["init"], "flush"),

            // seed (brain seed) — missing spine file.
            ("seed-missing-file", ["init"], "brain seed --from \"/no/such/directory/aitm-slice24-p4c-fixture-spine.json\""),

            // spine-import — missing spine file.
            ("spine-import-missing-file", ["init"], "spine-import --from \"/no/such/directory/aitm-slice24-p4c-fixture-spine.json\""),

            // eval — deterministic bare run on an empty store (facts table empty, so every answerable
            // question refuses/misses and every unanswerable one correctly refuses).
            ("eval-empty", ["init"], "eval"),
        ];
        foreach ((string name, string[] setup, string command) in cases)
            yield return new object[] { name, setup, command };
    }

    [Theory]
    [MemberData(nameof(Scenarios))]
    public void OldAndNewCliAgree(string _, string[] setup, string command) => AssertParity(setup, command);

    // stage node/triple/slot on a real ledger append, including the punctuated-key shapes (a hyphen,
    // slash, dot and underscore each get their own staged node key) — a wiring bug that dropped a flag or
    // mangled the JSON-escaping would show up as a divergent pending-learn.jsonl between old and new.
    [Fact]
    public void StageNodeTripleAndSlotMatchOldBehaviourAndLedger()
    {
        AssertParityAndLedger(["init"],
            "stage node p4c:node-key fact \"a short label\" --gloss \"a gloss\" --scheme test");

        foreach (string key in new[] { "p4c:hook-config", "p4c:hook/config", "p4c:hook.config", "p4c:hook_config" })
            AssertParityAndLedger(["init"], $"stage node {key} fact \"a short label\"");

        AssertParityAndLedger(["init"], "stage triple p4c:subject related p4c:object --because \"a fixture reason\"");
        AssertParityAndLedger(["init"], "stage slot p4c:frame name value --facet text");

        // list echoes back exactly what was appended.
        AssertParityAndLedger(["init", "stage node p4c:listed fact \"a short label\""], "stage list");
    }

    [Fact]
    public void StageClearAndDismissMatchOldBehaviourAfterARealStage()
    {
        AssertParityAndLedger(["init", "stage node p4c:to-clear fact \"a short label\""], "stage clear");
        AssertParityAndLedger(["init", "stage node p4c:to-dismiss fact \"a short label\""], "stage dismiss");
    }

    // flush — commits a real staged batch (node, triple, slot) in one transaction, then deletes the
    // ledger. Checked against both the resulting rows and the (now-empty) ledger file.
    [Fact]
    public void FlushMatchesOldBehaviourOnARealStagedBatch()
    {
        string[] setup =
        [
            "init",
            "stage node p4c:flush-subject fact \"subject label\"",
            "stage node p4c:flush-object fact \"object label\"",
            "stage triple p4c:flush-subject related p4c:flush-object --because \"a fixture reason\"",
            "stage slot p4c:flush-subject name value --facet text",
        ];
        (string oldInstance, string newInstance) = RunParity(setup, "flush");
        try
        {
            Assert.Equal(ReadLedgerOrEmpty(oldInstance), ReadLedgerOrEmpty(newInstance));
            foreach (string sql in new[] { NodeRowsSql, TripleRowsSql, SlotRowsSql, MutationRowsSql })
            {
                List<string> oldRows = SelectRows(AitmCliRunner.InstanceDbPath(oldInstance), sql);
                List<string> newRows = SelectRows(AitmCliRunner.InstanceDbPath(newInstance), sql);
                Assert.Equal(oldRows, newRows);
            }
        }
        finally
        {
            AitmCliRunner.DeleteInstance(oldInstance);
            AitmCliRunner.DeleteInstance(newInstance);
        }
    }

    // A full spine fixture (nodes, slots, links, aliases, terms, edges) shared by the seed and
    // spine-import scenarios below — the format SpineExportTool writes and SpineImportTool reads. Node
    // keys carry a hyphen, a slash, a dot and an underscore, covering the card's punctuation requirement
    // for the verbs that take free text via a file.
    private static string CreateSpineFixture()
    {
        string path = Path.Combine(Path.GetTempPath(), "aitm-slice24-p4c-spine-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, """
        {
          "nodes": [
            { "k": "p4c:spine-node", "kind": "fact", "label": "a spine node", "gloss": "a spine gloss", "scheme": "test", "hard": 0 },
            { "k": "p4c:spine/other", "kind": "fact", "label": "another spine node", "gloss": "", "scheme": "", "hard": 0 },
            { "k": "p4c:spine.frame", "kind": "codekind", "label": "a frame", "gloss": "", "scheme": "", "hard": 0 },
            { "k": "p4c:spine_frame_target", "kind": "fact", "label": "a slot target", "gloss": "", "scheme": "", "hard": 0 }
          ],
          "slots": [
            { "frame_k": "p4c:spine.frame", "name": "convention", "value": "p4c:spine_frame_target", "facet": "text", "multi": 0 }
          ],
          "links": [
            { "s": "p4c:spine-node", "p": "related", "o": "p4c:spine/other", "because": "a fixture reason" }
          ],
          "aliases": [
            { "short": "p4cspine", "k": "p4c:spine-node" }
          ],
          "terms": [
            { "term": "p4cterm", "canonical": "p4c:spine-node" }
          ],
          "edges": [
            { "symbol": "P4cFixtureWidget", "contract": "", "project": "server", "file": "src/api/p4c-widget.cs", "line": 3, "usage": "P4cFixtureWidget.Create()", "hardcoded": 0 }
          ]
        }
        """);
        return path;
    }

    [Fact]
    public void SpineImportMatchesOldBehaviourOnARealFixture()
    {
        string spine = CreateSpineFixture();
        try
        {
            AssertParityAndRows(["init"], $"spine-import --from \"{spine}\"",
                NodeRowsSql, TripleRowsSql, SlotRowsSql, AliasRowsSql, TermRowsSql, EdgeRowsSql);
        }
        finally
        {
            File.Delete(spine);
        }
    }

    [Fact]
    public void BrainSeedMatchesOldBehaviourOnARealFixture()
    {
        string spine = CreateSpineFixture();
        try
        {
            AssertParityAndRows(["init"], $"brain seed --from \"{spine}\"",
                NodeRowsSql, TripleRowsSql, SlotRowsSql, AliasRowsSql, TermRowsSql, EdgeRowsSql);
        }
        finally
        {
            File.Delete(spine);
        }
    }

    // spine-export writes a whole JSON file rather than a DB row, so its parity check is a byte-for-byte
    // diff of the two exported files, after seeding both stores identically via the spine fixture above
    // (so each export actually has nodes/links/slots/aliases/terms/edges to write, not an empty shell).
    [Fact]
    public void SpineExportMatchesOldBehaviourOnARealStore()
    {
        string spine = CreateSpineFixture();
        string oldInstance = AitmCliRunner.NewTestInstance("p4co-export");
        string newInstance = AitmCliRunner.NewTestInstance("p4cn-export");
        string oldOut = Path.Combine(Path.GetTempPath(), "aitm-slice24-p4c-export-old-" + Guid.NewGuid().ToString("N") + ".json");
        string newOut = Path.Combine(Path.GetTempPath(), "aitm-slice24-p4c-export-new-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            string oldDll = OldVsNewCli.OracleDll();
            string newDll = OldVsNewCli.BinCliDll();
            OldVsNewCli.Run(oldDll, oldInstance, "init");
            OldVsNewCli.Run(newDll, newInstance, "init");
            OldVsNewCli.Run(oldDll, oldInstance, $"spine-import --from \"{spine}\"");
            OldVsNewCli.Run(newDll, newInstance, $"spine-import --from \"{spine}\"");

            OldVsNewCli.Result oldResult = OldVsNewCli.Run(oldDll, oldInstance, $"spine-export --to \"{oldOut}\"");
            OldVsNewCli.Result newResult = OldVsNewCli.Run(newDll, newInstance, $"spine-export --to \"{newOut}\"");

            string oldStdout = StripVolatile(oldResult.Stdout.Replace(oldOut, "<out>"));
            string newStdout = StripVolatile(newResult.Stdout.Replace(newOut, "<out>"));
            Assert.Equal(oldStdout, newStdout);
            Assert.Equal(oldResult.Stderr, newResult.Stderr);
            Assert.Equal(oldResult.ExitCode, newResult.ExitCode);

            Assert.Equal(File.ReadAllText(oldOut), File.ReadAllText(newOut));
        }
        finally
        {
            File.Delete(spine);
            if (File.Exists(oldOut)) File.Delete(oldOut);
            if (File.Exists(newOut)) File.Delete(newOut);
            AitmCliRunner.DeleteInstance(oldInstance);
            AitmCliRunner.DeleteInstance(newInstance);
        }
    }

    // eval on real seeded facts (via the ordinary `add` verb) — a second, non-empty pass on top of the
    // bare-empty-store scenario above, so a wiring bug that dropped a search parameter would show up as
    // a divergent PASS/FAIL line rather than being masked by an all-refusal empty store.
    [Fact]
    public void EvalMatchesOldBehaviourOnRealSeededFacts()
    {
        string[] setup =
        [
            "init",
            "add --term p4c-eval-term --value \"the default server port is 7626\" --category manual --provenance stated",
        ];
        AssertParity(setup, "eval");
    }

    [GeneratedRegex(@"[ \t]*\d+[.,]\d+ms", RegexOptions.None, RegexTimeout.Milliseconds)]
    private static partial Regex PaddedTimingMs();

    [GeneratedRegex(@"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d+Z", RegexOptions.None, RegexTimeout.Milliseconds)]
    private static partial Regex IsoTimestamp();

    [GeneratedRegex(@"\(\d+[.,]\d+ms\)", RegexOptions.None, RegexTimeout.Milliseconds)]
    private static partial Regex ParenthesisedTimingMs();
}
