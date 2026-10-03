using System.Text.Json;
using Grimoira.Facts.Tools;
using Grimoira.Hooks.Data;
using Grimoira.Hooks.Tools;
using Grimoira.Store.Data;
using Grimoira.TestSupport;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Grimoira.Hooks.Tests;

/// <summary>
/// UserPromptSubmit recall: every prompt gets at most a few matching facts and rules as context, under a
/// hard 600-char budget, and nothing at all when the prompt is too short, a slash command, or matches
/// nothing. The compaction restore is a separate handler and is not under test here.
/// </summary>
public class PromptRecallToolTests
{
    private const int Budget = 600;

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
        add.Execute(connection, "login redirect", "[]", "web", "The web app login redirect goes through the auth callback route.", "spec", "", "verified", "test");
        add.Execute(connection, "encoder queue", "[]", "server", "The encoder queue stalls when ffmpeg holds the output file lock.", "spec", "", "verified", "test");
        add.Execute(connection, "subtitle parser", "[]", "server", "The subtitle parser reads ASS and VTT tracks.", "spec", "", "verified", "test");
        add.Execute(connection, "keycloak realm", "[]", "auth", "The keycloak realm name is nomercy.", "spec", "", "verified", "test");
        add.Execute(connection, "kmp deploy", "[]", "client", "Deploy the kmp app to the tv with its deploy script.", "spec", "", "verified", "test");
        add.Execute(connection, "cast receiver", "[]", "client", "The cast receiver reads its config from the receiver manifest.", "spec", "", "verified", "test");
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
            Assert.StartsWith("Grimoira recall for this prompt:", context);
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

    [Fact]
    public void OutputIsClippedTo600Chars()
    {
        string projectDir = NewTempProjectDir("clip");
        try
        {
            string instance = SeedDb(projectDir);
            // Six long facts that all match the same three terms: the raw recall is well over the budget.
            using (SqliteConnection connection = StoreConnection.Open(HookPaths.DbPath(instance)))
            {
                AddTool add = new();
                for (int i = 0; i < 6; i++)
                    add.Execute(connection, $"budget overflow sample {i}", "[]", "test", new string('x', 300) + " budget overflow sample", "spec", "", "verified", "test");
            }

            string? context = ContextOf(PromptRecallTool.Execute(Payload(projectDir, "budget overflow sample text"), projectDir));

            Assert.NotNull(context);
            Assert.True(context.Length <= Budget, $"context is {context.Length} chars");
            Assert.EndsWith("…", context);
            // Never cut mid-line: every line before the marker is a full line as the tools wrote it.
            Assert.DoesNotContain("xxx…", context);
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
}
