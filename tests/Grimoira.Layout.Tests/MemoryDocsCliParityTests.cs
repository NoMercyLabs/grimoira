using Grimoira.Store.Data;
using System.Text.RegularExpressions;
using Grimoira.TestSupport;
using Xunit;

namespace Grimoira.Layout.Tests;

// RESTRUCTURE.md slice 24, CLI lane part 2: proves the 12 Grimoira.Memory/Grimoira.Docs verbs behave exactly as
// they did before the wiring, by running each one on a fresh temp instance against the frozen "before"
// binary (OldVsNewCli.OracleDll, built from the commit slice 24 started at) and against today's
// bin-cli/grimoira.dll, and diffing stdout, stderr and exit code. Every wired verb gets at least a
// no-arg/help or normal run, and most also get a deliberate error case; mem/recall/doc (the query-taking
// verbs) each get a hyphen, slash, dot and underscore query on top of that.
//
// `redact-chat` is the one exception: GoldenListsTests' class comment records that grimoira.cs never had
// this verb before this part, so there is no "old" CLI behaviour to diff — the oracle binary answers
// "unknown command" for it. Its tests run only the new dll (RedactChatCliTests below), proving the CLI
// wiring (in particular --dry-run and the instance root threading through to the backup path) rather
// than old-vs-new parity.
//
// Every fixture used here (memory/docs/chat) lives under the OS temp dir, created and torn down by the
// test — never a real repo or ~/.claude — and any chat/redact-chat fixture text is an obviously fake
// token shape, never real data.
public partial class MemoryDocsCliParityTests
{
    private static string StripVolatile(string s) =>
        IsoTimestamp().Replace(ParenthesisedTimingMs().Replace(s, "(<ms>)"), "<ts>");

    private static (string stdout, string stderr, int exitCode) RunNormalized(
        OldVsNewCli.Result result, string instance, string dbPath)
    {
        string NormalizeOne(string s) => StripVolatile(s.Replace(instance, "<instance>").Replace(dbPath, "<db>"));
        return (NormalizeOne(result.Stdout), NormalizeOne(result.Stderr), result.ExitCode);
    }

    private static void AssertParity(string[] setup, string command)
    {
        string oldInstance = GrimoiraCliRunner.NewTestInstance("p2o");
        string newInstance = GrimoiraCliRunner.NewTestInstance("p2n");
        try
        {
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
        }
        finally
        {
            GrimoiraCliRunner.DeleteInstance(oldInstance);
            GrimoiraCliRunner.DeleteInstance(newInstance);
        }
    }

    public static IEnumerable<object[]> Scenarios()
    {
        (string name, string[] setup, string command)[] cases =
        [
            // mem — bare, --hard on an empty store, and a gap (no match).
            ("mem-bare", ["init"], "mem"),
            ("mem-hard-empty", ["init"], "mem --hard"),
            ("mem-gap", ["init"], "mem nothing-ever-matches-this"),
            ("mem-missing-nothing-needed", ["init"], "mem some query"),

            // index-memory — missing --from (ArgumentException) and a dir that does not exist.
            ("index-memory-missing-flag", ["init"], "index-memory"),
            ("index-memory-missing-dir", ["init"], "index-memory --from \"/no/such/directory/grimoira-slice24-fixture\""),

            // shed-memory — missing --key and a key that was never indexed.
            ("shed-memory-missing-flag", ["init"], "shed-memory"),
            ("shed-memory-miss", ["init"], "shed-memory --key never-existed"),

            // index-chat — missing --from and a transcript path that does not exist.
            ("index-chat-missing-flag", ["init"], "index-chat"),
            // index-chat's output changed when full-turn ingest replaced the user-only contract.

            // recall — bare and a gap (no match).
            ("recall-bare", ["init"], "recall"),
            // recall now reports the full match count and intentionally differs from this oracle.

            // doc — bare and a gap (no match).
            ("doc-bare", ["init"], "doc"),
            ("doc-gap", ["init"], "doc nothing-ever-matches-this"),

            // index-docs — missing --from and a root that does not exist (treated as one missing file,
            // so both old and new silently skip it rather than error).
            ("index-docs-missing-flag", ["init"], "index-docs"),
            ("index-docs-missing-root", ["init"], "index-docs --from \"/no/such/directory/grimoira-slice24-fixture\""),

            // recompact-docs — no args at all; the only shape it has is a bare run.
            ("recompact-docs-empty", ["init"], "recompact-docs"),

            // shed-doc — missing --path and a path that matches nothing.
            ("shed-doc-missing-flag", ["init"], "shed-doc"),
            ("shed-doc-miss", ["init"], "shed-doc --path never-existed"),

            // add-synthesis — missing --path/--from.
            ("add-synthesis-missing-flag", ["init"], "add-synthesis"),

            // shed-synthesis — missing --path and a dir that was never synthesised.
            ("shed-synthesis-missing-flag", ["init"], "shed-synthesis"),
            ("shed-synthesis-miss", ["init"], "shed-synthesis --path \"/no/such/directory/grimoira-slice24-fixture\""),
        ];
        foreach ((string name, string[] setup, string command) in cases)
            yield return [name, setup, command];
    }

    [Theory]
    [MemberData(nameof(Scenarios))]
    public void OldAndNewCliAgree(string _, string[] setup, string command) => AssertParity(setup, command);

    // index-memory needs a real fixture directory, so it (and mem, which reads what index-memory wrote)
    // get their own fixture rather than the shared setup list above.
    [Fact]
    public void IndexMemoryAndMemMatchOldBehaviourOnARealFixture()
    {
        string dir = MakeTempDir("grimoira-slice24-mem-fixture");
        File.WriteAllText(Path.Combine(dir, "hook-config.md"), """
            ---
            type: feedback
            name: Hook Config Memory
            description: hook config memfixtureterm description
            ---
            Body text about hook config memfixtureterm behavior for testing, never a real rule.
            """);
        try
        {
            AssertParity(["init"], $"index-memory --from \"{dir}\"");
            AssertParity(["init", $"index-memory --from \"{dir}\""], "mem memfixtureterm");
            AssertParity(["init", $"index-memory --from \"{dir}\""], "shed-memory --key hook-config");

            // The query-taking verb needs a hyphen, slash, dot and underscore query — a tokenizer
            // difference on the CLI side would show up as a divergent hit count between old and new.
            foreach (string query in new[] { "hook-config", "hook/config", "hook.config", "hook_config" })
                AssertParity(["init", $"index-memory --from \"{dir}\""], $"mem {query}");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // index-chat needs a real transcript file. The text is an obviously fake token shape (never real
    // chat), which also exercises the same secret-scrub path index-chat always ran through.
    [Fact]
    public void IndexChatAndRecallUseTheFullTurnContractOnARealFixture()
    {
        string dir = MakeTempDir("grimoira-slice24-chat-fixture");
        string file = Path.Combine(dir, "grimoira-slice24-chat-fixture.jsonl");
        File.WriteAllText(file,
            """{"type":"user","message":{"content":"hook config recallfixtureterm entry documents fixture behavior for slice twenty four testing purposes only, never a real conversation, containing a fake token ghp_1234567890abcdefghijklmnopqrstuvwx for scrub coverage"},"uuid":"fixture-uuid-1","timestamp":"2026-01-01T00:00:00.000Z"}""" + "\n");
        try
        {
            string instance = GrimoiraCliRunner.NewTestInstance("full-chat-parity");
            try
            {
                GrimoiraCliRunner.Seed($"init --instance {instance}");
                (string indexed, int indexExit) = GrimoiraCliRunner.Seed($"index-chat --instance {instance} --from \"{file}\"");
                Assert.Equal(0, indexExit);
                Assert.Contains("1 turn(s) indexed", indexed);
                (string recalled, int recallExit) = GrimoiraCliRunner.Seed($"recall --instance {instance} recallfixtureterm");
                Assert.Equal(0, recallExit);
                Assert.Contains("1 total; showing 1; remaining 0", recalled);
                Assert.DoesNotContain("ghp_1234567890abcdefghijklmnopqrstuvwx", recalled);
            }
            finally { GrimoiraCliRunner.DeleteInstance(instance); }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // index-docs needs a real markdown file. doc and shed-doc then read what it absorbed.
    [Fact]
    public void IndexDocsDocAndShedDocMatchOldBehaviourOnARealFixture()
    {
        string dir = MakeTempDir("grimoira-slice24-docs-fixture");
        string file = Path.Combine(dir, "hook-config.md");
        File.WriteAllText(file, """
            # Hook Config

            hook config docfixtureterm behavior for testing absorption and chunking, never a real doc.
            """);
        try
        {
            AssertParity(["init"], $"index-docs --from \"{file}\"");
            AssertParity(["init", $"index-docs --from \"{file}\""], "doc docfixtureterm");
            AssertParity(["init", $"index-docs --from \"{file}\""], "shed-doc --path hook-config");

            foreach (string query in new[] { "hook-config", "hook/config", "hook.config", "hook_config" })
                AssertParity(["init", $"index-docs --from \"{file}\""], $"doc {query}");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // add-synthesis and shed-synthesis need a source dir + a body file to distill (mirrors
    // StoreFactsCliParityTests' backup/import fixtures: each binary needs its own instance, but the
    // filesystem fixture is read-only, so both binaries can safely share it).
    [Fact]
    public void AddSynthesisAndShedSynthesisMatchOldBehaviourOnARealFixture()
    {
        string dir = MakeTempDir("grimoira-slice24-synthesis-fixture");
        string bodyFile = Path.Combine(dir, "body.md");
        File.WriteAllText(bodyFile, string.Concat(Enumerable.Repeat(
            "This orientation brief distills the answer several fixture files agreed on, never real content. ",
            8)));
        try
        {
            AssertParity(["init"],
                $"add-synthesis --path \"{dir}\" --from \"{bodyFile}\" --title \"fixture brief\" --sources \"{bodyFile}\"");
            AssertParity(
                [$"add-synthesis --path \"{dir}\" --from \"{bodyFile}\" --title \"fixture brief\" --sources \"{bodyFile}\""],
                $"shed-synthesis --path \"{dir}\"");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // add-synthesis's other normal shape: a body too short to be worth storing.
    [Fact]
    public void AddSynthesisMatchesOldBehaviourWhenTheBodyIsTooShort()
    {
        string dir = MakeTempDir("grimoira-slice24-synthesis-short-fixture");
        string bodyFile = Path.Combine(dir, "short.md");
        File.WriteAllText(bodyFile, "too short, never real content.");
        try
        {
            AssertParity(["init"], $"add-synthesis --path \"{dir}\" --from \"{bodyFile}\"");
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

// redact-chat never had an grimoira.cs case before this part (no oracle behaviour exists to diff against —
// see the class comment above), so these run only the new dll and prove the wiring: --dry-run is parsed,
// the instance root threads through to the VACUUM INTO backup, and a fake token-shaped row gets scrubbed.
// Never real chat data, per the same rule index-chat's fixture follows.
public class RedactChatCliTests
{
    [Fact]
    public void BareRunOnAnEmptyStoreBacksUpAndReportsZeroRows()
    {
        string instance = GrimoiraCliRunner.NewTestInstance("p2-redact-bare");
        try
        {
            GrimoiraCliRunner.Run($"init --instance {instance}");
            (string stdout, int exitCode) = GrimoiraCliRunner.Run($"redact-chat --instance {instance}");

            Assert.Equal(0, exitCode);
            Assert.Contains("backup ->", stdout);
            Assert.Contains("redacted 0 row(s)", stdout);
        }
        finally
        {
            GrimoiraCliRunner.DeleteInstance(instance);
        }
    }

    [Fact]
    public void NormalRunScrubsAFakeTokenShapedRowAndReportsItsKind()
    {
        string instance = GrimoiraCliRunner.NewTestInstance("p2-redact-normal");
        try
        {
            GrimoiraCliRunner.Run($"init --instance {instance}");
            InsertFakeChatRow(instance, "p2-redact-session:k1",
                "leaked fixture token ghp_1234567890abcdefghijklmnopqrstuvwx here, never a real secret");

            (string stdout, int exitCode) = GrimoiraCliRunner.Run($"redact-chat --instance {instance}");

            Assert.Equal(0, exitCode);
            Assert.Contains("backup ->", stdout);
            Assert.Contains("redacted 1 row(s)", stdout);
            Assert.Contains("github: 1", stdout);
        }
        finally
        {
            GrimoiraCliRunner.DeleteInstance(instance);
        }
    }

    [Fact]
    public void DryRunReportsButLeavesTheRowUnchanged()
    {
        string instance = GrimoiraCliRunner.NewTestInstance("p2-redact-dry-run");
        try
        {
            GrimoiraCliRunner.Run($"init --instance {instance}");
            string jwt = "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.SflKxwRJSMeKKF2QT4fwpMeJf36POk6yJV_adQssw5c";
            InsertFakeChatRow(instance, "p2-redact-dry-session:k1", $"fixture-only leaked jwt {jwt} here");

            (string stdout, int exitCode) = GrimoiraCliRunner.Run($"redact-chat --dry-run --instance {instance}");

            Assert.Equal(0, exitCode);
            Assert.Contains("dry run: 1 row(s) would be redacted", stdout);
            Assert.Contains("jwt: 1", stdout);

            string dbPath = GrimoiraCliRunner.InstanceDbPath(instance);
            using Microsoft.Data.Sqlite.SqliteConnection check = new($"Data Source={dbPath};Mode=ReadOnly;Pooling=False");
            check.Open();
            using Microsoft.Data.Sqlite.SqliteCommand select = check.CreateCommand();
            select.CommandText = "SELECT text FROM chat WHERE k='p2-redact-dry-session:k1'";
            string stored = (string)select.ExecuteScalar()!;
            Assert.Contains(jwt, stored);
        }
        finally
        {
            GrimoiraCliRunner.DeleteInstance(instance);
        }
    }

    private static void InsertFakeChatRow(string instance, string key, string text)
    {
        string dbPath = GrimoiraCliRunner.InstanceDbPath(instance);
        using Microsoft.Data.Sqlite.SqliteConnection connection = new($"Data Source={dbPath};Pooling=False");
        connection.Open();
        using Microsoft.Data.Sqlite.SqliteCommand insert = connection.CreateCommand();
        insert.CommandText = "INSERT INTO chat(k,session,ts,role,text) VALUES($k,'p2-redact-session','2026-09-25T12:00:00.000Z','user',$tx)";
        insert.Parameters.AddWithValue("$k", key);
        insert.Parameters.AddWithValue("$tx", text);
        insert.ExecuteNonQuery();
    }
}
