using System.Text.Json;
using Grimoira.Hooks.Data;
using Grimoira.Hooks.Tools;
using Grimoira.Store.Data;
using Grimoira.TestSupport;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Grimoira.Hooks.Tests;

/// <summary>
/// PreToolUse edit gate: the first edit of a file in a session gets the rules and facts that match the
/// file's path as context; a later edit of the same file in the same session gets nothing; the hook
/// never blocks the edit (empty output on every missing input).
/// </summary>
public class EditGateToolTests
{
    private const string FilePath = "src/Billing/InvoiceService.cs";

    [Fact]
    public void FirstEditOfFileReturnsRulesAndFacts()
    {
        string instance = GrimoiraCliRunner.NewTestInstance("edit-gate-first");
        try
        {
            string projectDir = ProjectDirFor(instance);
            SeedRule(instance, "rule-invoice", "Invoice rounding", "billing invoice service rounds half up");
            SeedFact(instance, "InvoiceService", "billing invoice service owns the rounding");

            string output = EditGateTool.Execute(Payload("s1", projectDir, FilePath), projectDir);

            using JsonDocument doc = JsonDocument.Parse(output);
            JsonElement hook = doc.RootElement.GetProperty("hookSpecificOutput");
            Assert.Equal("PreToolUse", hook.GetProperty("hookEventName").GetString());
            string context = hook.GetProperty("additionalContext").GetString()!;
            Assert.Contains("before you edit", context);
            Assert.Contains("rounds half up", context);
            Assert.Contains("owns the rounding", context);
        }
        finally
        {
            GrimoiraCliRunner.DeleteInstance(instance);
        }
    }

    [Fact]
    public void SecondEditOfSameFileInSameSessionReturnsEmpty()
    {
        string instance = GrimoiraCliRunner.NewTestInstance("edit-gate-second");
        try
        {
            string projectDir = ProjectDirFor(instance);
            SeedRule(instance, "rule-invoice", "Invoice rounding", "billing invoice service rounds half up");

            string first = EditGateTool.Execute(Payload("s1", projectDir, FilePath), projectDir);
            string second = EditGateTool.Execute(Payload("s1", projectDir, FilePath.ToUpperInvariant()), projectDir);

            Assert.NotEqual("", first);
            Assert.Equal("", second);
        }
        finally
        {
            GrimoiraCliRunner.DeleteInstance(instance);
        }
    }

    [Fact]
    public void SameFileInAnotherSessionReturnsContext()
    {
        string instance = GrimoiraCliRunner.NewTestInstance("edit-gate-other-session");
        try
        {
            string projectDir = ProjectDirFor(instance);
            SeedRule(instance, "rule-invoice", "Invoice rounding", "billing invoice service rounds half up");

            EditGateTool.Execute(Payload("s1", projectDir, FilePath), projectDir);
            string other = EditGateTool.Execute(Payload("s2", projectDir, FilePath), projectDir);

            Assert.Contains("rounds half up", other);
        }
        finally
        {
            GrimoiraCliRunner.DeleteInstance(instance);
        }
    }

    [Fact]
    public void NoMatchReturnsEmptyAndMarksSeen()
    {
        string instance = GrimoiraCliRunner.NewTestInstance("edit-gate-nomatch");
        try
        {
            string projectDir = ProjectDirFor(instance);
            GrimoiraCliRunner.Seed($"init --instance {instance}");

            string output = EditGateTool.Execute(Payload("s1", projectDir, FilePath), projectDir);

            Assert.Equal("", output);
            string seen = File.ReadAllText(HookPaths.GateSeenPath(instance, "s1"));
            Assert.Contains("invoiceservice.cs", seen.ToLowerInvariant());
        }
        finally
        {
            GrimoiraCliRunner.DeleteInstance(instance);
        }
    }

    [Fact]
    public void OutputIsClippedTo1500Chars()
    {
        string instance = GrimoiraCliRunner.NewTestInstance("edit-gate-clip");
        try
        {
            string projectDir = ProjectDirFor(instance);
            string longBody = string.Join(' ', Enumerable.Repeat("billing invoice service rounds half up", 40));
            for (int i = 0; i < 4; i++) SeedRule(instance, $"rule-{i}", $"Invoice rule {i}", longBody);
            for (int i = 0; i < 3; i++) SeedFact(instance, $"InvoiceService{i}", longBody);

            string output = EditGateTool.Execute(Payload("s1", projectDir, FilePath), projectDir);

            using JsonDocument doc = JsonDocument.Parse(output);
            string context = doc.RootElement.GetProperty("hookSpecificOutput").GetProperty("additionalContext").GetString()!;
            Assert.True(context.Length <= 1500, $"context is {context.Length} chars");
            Assert.EndsWith("…", context);
        }
        finally
        {
            GrimoiraCliRunner.DeleteInstance(instance);
        }
    }

    [Fact]
    public void MissingFilePathReturnsEmpty()
    {
        string instance = GrimoiraCliRunner.NewTestInstance("edit-gate-nopath");
        try
        {
            string projectDir = ProjectDirFor(instance);
            SeedRule(instance, "rule-invoice", "Invoice rounding", "billing invoice service rounds half up");

            string output = EditGateTool.Execute($$$"""{"session_id":"s1","cwd":{{{Json(projectDir)}}},"tool_name":"Edit","tool_input":{}}""", projectDir);

            Assert.Equal("", output);
        }
        finally
        {
            GrimoiraCliRunner.DeleteInstance(instance);
        }
    }

    [Fact]
    public void MissingDbReturnsEmpty()
    {
        string instance = GrimoiraCliRunner.NewTestInstance("edit-gate-nodb");
        try
        {
            string projectDir = ProjectDirFor(instance);

            string output = EditGateTool.Execute(Payload("s1", projectDir, FilePath), projectDir);

            Assert.Equal("", output);
            Assert.False(File.Exists(HookPaths.DbPath(instance)));
        }
        finally
        {
            GrimoiraCliRunner.DeleteInstance(instance);
        }
    }

    [Fact]
    public void GateLogLineWritten()
    {
        string instance = GrimoiraCliRunner.NewTestInstance("edit-gate-log");
        try
        {
            string projectDir = ProjectDirFor(instance);
            SeedRule(instance, "rule-invoice", "Invoice rounding", "billing invoice service rounds half up");

            EditGateTool.Execute(Payload("s1", projectDir, FilePath), projectDir);

            string log = File.ReadAllText(Path.Combine(HookPaths.InstanceDir(instance), "gate.log"));
            Assert.Contains(" PreToolUse s1 ", log);
            Assert.Contains("rules=1 facts=0 chars=", log);
        }
        finally
        {
            GrimoiraCliRunner.DeleteInstance(instance);
        }
    }

    // The instance is the slug of the project folder's name (HookPaths.ResolveInstance), so a project dir
    // named after the test instance routes the gate to that instance's store.
    private static string ProjectDirFor(string instance) => Path.Combine(Path.GetTempPath(), instance);

    private static string Payload(string sessionId, string projectDir, string relativePath) =>
        $$$"""{"session_id":{{{Json(sessionId)}}},"cwd":{{{Json(projectDir)}}},"tool_name":"Edit","tool_input":{"file_path":{{{Json(Path.Combine(projectDir, relativePath))}}}}}""";

    private static string Json(string value) => JsonSerializer.Serialize(value);

    private static void SeedRule(string instance, string key, string title, string body)
    {
        if (!File.Exists(HookPaths.DbPath(instance))) GrimoiraCliRunner.Seed($"init --instance {instance}");
        using SqliteConnection connection = StoreConnection.Open(HookPaths.DbPath(instance));
        using SqliteCommand insert = connection.CreateCommand();
        insert.CommandText = "INSERT INTO memory(k,type,title,hook,body,links,hard) VALUES($k,'feedback',$t,$t,$b,'',0)";
        insert.Parameters.AddWithValue("$k", key);
        insert.Parameters.AddWithValue("$t", title);
        insert.Parameters.AddWithValue("$b", body);
        insert.ExecuteNonQuery();
        using SqliteCommand fts = connection.CreateCommand();
        fts.CommandText = "INSERT INTO memory_fts(k,title,hook,body) VALUES($k,$t,$t,$b)";
        fts.Parameters.AddWithValue("$k", key);
        fts.Parameters.AddWithValue("$t", title);
        fts.Parameters.AddWithValue("$b", body);
        fts.ExecuteNonQuery();
    }

    private static void SeedFact(string instance, string term, string value)
    {
        if (!File.Exists(HookPaths.DbPath(instance))) GrimoiraCliRunner.Seed($"init --instance {instance}");
        using SqliteConnection connection = StoreConnection.Open(HookPaths.DbPath(instance));
        using SqliteCommand insert = connection.CreateCommand();
        insert.CommandText = "INSERT INTO facts(k,term,aliases,category,value,source,notes) VALUES($k,$t,'','manual',$v,'test','')";
        insert.Parameters.AddWithValue("$k", term.ToLowerInvariant());
        insert.Parameters.AddWithValue("$t", term);
        insert.Parameters.AddWithValue("$v", value);
        insert.ExecuteNonQuery();
        using SqliteCommand fts = connection.CreateCommand();
        fts.CommandText = "INSERT INTO facts_fts(k,term,aliases,category,value,notes) VALUES($k,$t,'','manual',$v,'')";
        fts.Parameters.AddWithValue("$k", term.ToLowerInvariant());
        fts.Parameters.AddWithValue("$t", term);
        fts.Parameters.AddWithValue("$v", value);
        fts.ExecuteNonQuery();
    }
}
