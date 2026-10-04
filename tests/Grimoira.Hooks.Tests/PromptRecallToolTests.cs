using System.Diagnostics;
using System.Text.Json;
using Grimoira.Docs.Tools;
using Grimoira.Facts.Tools;
using Grimoira.Hooks.Data;
using Grimoira.Hooks.Tools;
using Grimoira.Store.Data;
using Grimoira.TestSupport;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Grimoira.Hooks.Tests;

/// <summary>
/// UserPromptSubmit recall: every prompt gets at most three matching docs, facts and rules as one-line
/// hits, under a hard 1,200-char budget, and nothing at all when the prompt is too short, a slash command,
/// or matches nothing. The compaction restore is a separate handler; only its merge with the recall into
/// one envelope is pinned here.
/// </summary>
public class PromptRecallToolTests
{
    private const int Budget = 1200;
    private const int MaxHits = 3;
    private const string Header = "Grimoira (top hits for this prompt; cite or open them before reading other files):";

    private static string NewTempProjectDir(string label)
    {
        // The instance name is the basename of "cwd" (HookPaths.ResolveInstance); "test-" lets the shared
        // cleanup guard remove it.
        string dir = Path.Combine(Path.GetTempPath(), $"test-recall-{label}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string Payload(string projectDir, string prompt, string sessionId = "s1") =>
        JsonSerializer.Serialize(new { cwd = projectDir, session_id = sessionId, prompt });

    private static string SeedDb(string projectDir)
    {
        string instance = HookPaths.ResolveInstance(projectDir);
        GrimoiraCliRunner.Seed($"init --instance {instance}");
        using SqliteConnection connection = StoreConnection.Open(HookPaths.DbPath(instance));
        AddTool add = new();
        add.Execute(connection, "login redirect", "[]", "web", "The web app login redirect goes through the auth callback route.", "spec", "", "stated", "test");
        add.Execute(connection, "encoder queue", "[]", "server", "The encoder queue stalls when ffmpeg holds the output file lock.", "spec", "", "stated", "test");
        add.Execute(connection, "subtitle parser", "[]", "server", "The subtitle parser reads ASS and VTT tracks.", "spec", "", "stated", "test");
        add.Execute(connection, "keycloak realm", "[]", "auth", "The keycloak realm name is nomercy.", "spec", "", "stated", "test");
        add.Execute(connection, "kmp deploy", "[]", "client", "Deploy the kmp app to the tv with its deploy script.", "spec", "", "stated", "test");
        add.Execute(connection, "cast receiver", "[]", "client", "The cast receiver reads its config from the receiver manifest.", "spec", "", "stated", "test");
        string[][] rules =
        [
            ["r-login", "login redirect test", "Every login redirect change gets a redirect test on the web app."],
            ["r-encoder", "encoder queue stall", "Never restart the encoder queue to clear a stall; find the lock."],
            ["r-subtitle", "subtitle parser fixtures", "A subtitle parser test uses real track fixtures."],
            ["r-keycloak", "keycloak realm edits", "Never edit the keycloak realm by hand; use the export."],
            ["r-kmp", "kmp deploy script", "Deploy the kmp app only with its deploy script."],
            ["r-cast", "cast receiver config", "The cast receiver config is read once at launch."],
        ];
        foreach (string[] rule in rules)
        {
            using SqliteCommand mem = connection.CreateCommand();
            mem.CommandText = "INSERT INTO memory(k,type,title,hook,body,links,hard) VALUES($k,'feedback',$t,$h,$b,'',0)";
            mem.Parameters.AddWithValue("$k", rule[0]);
            mem.Parameters.AddWithValue("$t", rule[1]);
            mem.Parameters.AddWithValue("$h", rule[1]);
            mem.Parameters.AddWithValue("$b", rule[2]);
            mem.ExecuteNonQuery();
            using SqliteCommand fts = connection.CreateCommand();
            fts.CommandText = "INSERT INTO memory_fts(k,title,hook,body) VALUES($k,$t,$h,$b)";
            fts.Parameters.AddWithValue("$k", rule[0]);
            fts.Parameters.AddWithValue("$t", rule[1]);
            fts.Parameters.AddWithValue("$h", rule[1]);
            fts.Parameters.AddWithValue("$b", rule[2]);
            fts.ExecuteNonQuery();
        }
        return instance;
    }

    private static string? ContextOf(string output)
    {
        if (output.Length == 0) return null;
        using JsonDocument doc = JsonDocument.Parse(output);
        JsonElement hso = doc.RootElement.GetProperty("hookSpecificOutput");
        Assert.Equal("UserPromptSubmit", hso.GetProperty("hookEventName").GetString());
        return hso.GetProperty("additionalContext").GetString();
    }

    private static void Cleanup(string projectDir)
    {
        GrimoiraCliRunner.DeleteInstance(HookPaths.ResolveInstance(projectDir));
        try { Directory.Delete(projectDir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void PromptWithMatchingFactReturnsIt()
    {
        string projectDir = NewTempProjectDir("fact");
        try
        {
            SeedDb(projectDir);
            string? context = ContextOf(PromptRecallTool.Execute(Payload(projectDir, "fix the login redirect on the web app"), projectDir));

            Assert.NotNull(context);
            Assert.StartsWith(Header, context);
            Assert.Contains("[fact] login redirect", context);
            Assert.Contains("auth callback route", context);
        }
        finally
        {
            Cleanup(projectDir);
        }
    }

    [Fact]
    public void PromptWithMatchingRuleReturnsIt()
    {
        string projectDir = NewTempProjectDir("rule");
        try
        {
            SeedDb(projectDir);
            string? context = ContextOf(PromptRecallTool.Execute(Payload(projectDir, "why does the encoder queue stall"), projectDir));

            Assert.NotNull(context);
            Assert.Contains("find the lock", context);
        }
        finally
        {
            Cleanup(projectDir);
        }
    }

    [Fact]
    public void NoMatchReturnsEmpty()
    {
        string projectDir = NewTempProjectDir("nomatch");
        try
        {
            SeedDb(projectDir);
            Assert.Equal("", PromptRecallTool.Execute(Payload(projectDir, "zebra quartz umbrella violin"), projectDir));
        }
        finally
        {
            Cleanup(projectDir);
        }
    }

    [Fact]
    public void FewerThanThreeTermsReturnsEmpty()
    {
        string projectDir = NewTempProjectDir("short");
        try
        {
            SeedDb(projectDir);
            Assert.Equal("", PromptRecallTool.Execute(Payload(projectDir, "ok"), projectDir));
            Assert.Equal("", PromptRecallTool.Execute(Payload(projectDir, "encoder queue"), projectDir));
            Assert.Equal("", PromptRecallTool.Execute(Payload(projectDir, "run the tests"), projectDir));
        }
        finally
        {
            Cleanup(projectDir);
        }
    }

    [Fact]
    public void SlashCommandReturnsEmpty()
    {
        string projectDir = NewTempProjectDir("slash");
        try
        {
            SeedDb(projectDir);
            Assert.Equal("", PromptRecallTool.Execute(Payload(projectDir, "/help login redirect web app"), projectDir));
            Assert.Equal("", PromptRecallTool.Execute(Payload(projectDir, "<system>login redirect web app</system>"), projectDir));
        }
        finally
        {
            Cleanup(projectDir);
        }
    }

    private static string IndexDoc(string instance, string projectDir, string name, string title, string body)
    {
        string file = Path.Combine(projectDir, name);
        File.WriteAllText(file, $"# {title}\n\n{body}\n");
        using SqliteConnection connection = StoreConnection.Open(HookPaths.DbPath(instance));
        new IndexDocsTool().Execute(connection, file, "doc");
        return Path.GetFullPath(file).Replace('\\', '/');
    }

    private static string[] HitLines(string context)
    {
        string[] lines = context.Split('\n');
        Assert.Equal(Header, lines[0]);
        return lines[1..];
    }

    [Fact]
    public void PromptMatchingAnIndexedDocReturnsItsTitleAndPath()
    {
        string projectDir = NewTempProjectDir("doc");
        try
        {
            string instance = SeedDb(projectDir);
            string path = IndexDoc(instance, projectDir, "release-checklist.md", "Release checklist for the player",
                "Bump the version, run the full suite, then publish the player package from the release branch.");

            string? context = ContextOf(PromptRecallTool.Execute(Payload(projectDir, "how do we publish the player package release"), projectDir));

            Assert.NotNull(context);
            Assert.StartsWith(Header, context);
            string line = Assert.Single(HitLines(context), l => l.StartsWith("[doc] ", StringComparison.Ordinal));
            Assert.StartsWith($"[doc] Release checklist for the player ({path}) — ", line);
            Assert.Contains("publish the player package", line);
        }
        finally
        {
            Cleanup(projectDir);
        }
    }

    [Fact]
    public void RecallKeepsAtMostThreeOneLineHitsUnderTheBudget()
    {
        string projectDir = NewTempProjectDir("caps");
        try
        {
            string instance = SeedDb(projectDir);
            // Six long docs and six long facts that all match the same three terms: far more than three hits,
            // each far longer than one line, so both caps have to bite.
            for (int i = 0; i < 6; i++)
                IndexDoc(instance, projectDir, $"overflow-{i}.md", $"Budget overflow sample {i}", new string('x', 600) + " budget overflow sample");
            using (SqliteConnection connection = StoreConnection.Open(HookPaths.DbPath(instance)))
            {
                AddTool add = new();
                for (int i = 0; i < 6; i++)
                    add.Execute(connection, $"budget overflow sample {i}", "[]", "test", new string('y', 600) + " budget overflow sample", "spec", "", "stated", "test");
            }

            string? context = ContextOf(PromptRecallTool.Execute(Payload(projectDir, "budget overflow sample text"), projectDir));

            Assert.NotNull(context);
            Assert.True(context.Length <= Budget, $"context is {context.Length} chars");
            string[] hits = HitLines(context);
            Assert.True(hits.Length is > 0 and <= MaxHits, $"{hits.Length} hit lines");
            Assert.All(hits, hit => Assert.Matches(@"^\[(doc|fact|memory)\] .+ — .+$", hit));
            Assert.All(hits, hit => Assert.True(hit.Length <= 420, $"hit line is {hit.Length} chars"));
        }
        finally
        {
            Cleanup(projectDir);
        }
    }

    [Fact]
    public void PendingCompactionBriefAndRecallLandInOneEnvelope()
    {
        string projectDir = NewTempProjectDir("merge");
        try
        {
            SeedDb(projectDir);
            string transcript = Path.Combine(projectDir, "transcript.jsonl");
            File.WriteAllLines(transcript,
            [
                JsonSerializer.Serialize(new { type = "user", message = new { content = "Fix the login redirect loop." } }),
            ]);
            CompactBriefTool.Execute(JsonSerializer.Serialize(new { transcript_path = transcript, cwd = projectDir, session_id = "sess-m" }));
            string prompt = Payload(projectDir, "fix the login redirect on the web app", "sess-m");

            string merged = HookEnvelope.Merge(CompactRestoreTool.Execute(prompt, projectDir), PromptRecallTool.Execute(prompt, projectDir));

            string? context = ContextOf(merged);
            Assert.NotNull(context);
            Assert.Contains("Fix the login redirect loop.", context);
            Assert.Contains(Header, context);
            Assert.Contains("auth callback route", context);
            Assert.True(context.IndexOf("Fix the login redirect loop.", StringComparison.Ordinal) < context.IndexOf(Header, StringComparison.Ordinal));
        }
        finally
        {
            Cleanup(projectDir);
        }
    }

    [Fact]
    public void RecallOverTenThousandDocSectionsAnswersWellInsideTheHookDeadline()
    {
        string projectDir = NewTempProjectDir("big");
        try
        {
            string instance = SeedDb(projectDir);
            using (SqliteConnection connection = StoreConnection.Open(HookPaths.DbPath(instance)))
            {
                using SqliteTransaction tx = connection.BeginTransaction();
                for (int i = 0; i < 10_000; i++)
                {
                    string k = $"big/doc-{i / 20}.md#{i % 20}";
                    string title = $"Section {i} on topic {i % 97}";
                    string content = $"Section {i} describes topic {i % 97} and the pipeline step {i % 13} in the generated corpus.";
                    using SqliteCommand doc = connection.CreateCommand();
                    doc.Transaction = tx;
                    doc.CommandText = "INSERT INTO docs(k,path,title,category,content,terms) VALUES($k,$p,$t,'doc',$c,'')";
                    doc.Parameters.AddWithValue("$k", k);
                    doc.Parameters.AddWithValue("$p", $"big/doc-{i / 20}.md");
                    doc.Parameters.AddWithValue("$t", title);
                    doc.Parameters.AddWithValue("$c", content);
                    doc.ExecuteNonQuery();
                    using SqliteCommand fts = connection.CreateCommand();
                    fts.Transaction = tx;
                    fts.CommandText = "INSERT INTO docs_fts(k,title,content) VALUES($k,$t,$c)";
                    fts.Parameters.AddWithValue("$k", k);
                    fts.Parameters.AddWithValue("$t", title);
                    fts.Parameters.AddWithValue("$c", content);
                    fts.ExecuteNonQuery();
                }
                tx.Commit();
            }

            Stopwatch clock = Stopwatch.StartNew();
            string? context = ContextOf(PromptRecallTool.Execute(Payload(projectDir, "generated corpus pipeline step topic"), projectDir));
            clock.Stop();

            Assert.NotNull(context);
            Assert.Contains("[doc] Section ", context);
            // The CLI forwards with a 3 s deadline and Claude Code allows 10 s; the recall itself gets 2 s.
            Assert.True(clock.ElapsedMilliseconds < 2000, $"recall over 10,000 sections took {clock.ElapsedMilliseconds} ms");
            File.WriteAllText(Path.Combine(projectDir, "..", "test-recall-timing.txt"), $"recall over 10,000 doc sections: {clock.ElapsedMilliseconds} ms");
        }
        finally
        {
            Cleanup(projectDir);
        }
    }

    [Fact]
    public void MissingDbReturnsEmpty()
    {
        string projectDir = NewTempProjectDir("nodb");
        try
        {
            Assert.False(File.Exists(HookPaths.DbPath(HookPaths.ResolveInstance(projectDir))));
            Assert.Equal("", PromptRecallTool.Execute(Payload(projectDir, "fix the login redirect on the web app"), projectDir));
        }
        finally
        {
            Cleanup(projectDir);
        }
    }

    [Fact]
    public void RecallLogLineWritten()
    {
        string projectDir = NewTempProjectDir("log");
        try
        {
            string instance = SeedDb(projectDir);
            string output = PromptRecallTool.Execute(Payload(projectDir, "fix the login redirect on the web app", "sess-log"), projectDir);
            PromptRecallTool.Execute(Payload(projectDir, "ok", "sess-log"), projectDir);

            Assert.NotEqual("", output);
            string[] lines = File.ReadAllLines(Path.Combine(HookPaths.InstanceDir(instance), "recall.log"));
            string line = Assert.Single(lines);
            Assert.Contains(" UserPromptSubmit sess-log terms=", line);
            Assert.Contains(" chars=", line);
            Assert.True(DateTime.TryParse(line.Split(' ')[0], null, System.Globalization.DateTimeStyles.RoundtripKind, out _), line);
        }
        finally
        {
            Cleanup(projectDir);
        }
    }

    [Fact]
    public void PausedRecallReturnsEmpty()
    {
        string projectDir = NewTempProjectDir("paused");
        try
        {
            string instance = SeedDb(projectDir);
            GateSwitch.Off(instance, "test");

            Assert.Equal("", PromptRecallTool.Execute(Payload(projectDir, "fix the login redirect on the web app"), projectDir));
        }
        finally
        {
            Cleanup(projectDir);
        }
    }
}
