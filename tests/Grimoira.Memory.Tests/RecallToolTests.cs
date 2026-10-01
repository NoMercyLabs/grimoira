using System.Reflection;
using Grimoira.Memory.Tools;
using Grimoira.Store.Data;
using Grimoira.TestSupport;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Grimoira.Memory.Tests;

public class RecallToolTests
{
    [Fact]
    public void CliShapeMatchesTodaysCliOutputForAConfidentMatch()
    {
        string instance = GrimoiraCliRunner.NewTestInstance("recall-cli-hit");
        // The fixture text must contain the exact query term below ("recallclitopic") for chat_fts to
        // register a real hit — MakeFixtureTranscript builds its text as `session + "topic"`, so the
        // session here has to be "recallcli", not "recallclihit" (that extra "hit" made the indexed
        // token "recallclihittopic", which the query below never matched — the fixture stayed under a
        // confident match and both assertions passed on the "no chat history matches" gap text instead,
        // since that text also echoes the query term back verbatim).
        string transcript = MakeFixtureTranscript("recallcli");
        try
        {
            GrimoiraCliRunner.Run($"init --instance {instance}");
            GrimoiraCliRunner.Seed($"index-chat --instance {instance} --from \"{transcript}\"");

            (string stdout, int exitCode) = GrimoiraCliRunner.Seed($"recall --instance {instance} recallclitopic");
            Assert.Equal(0, exitCode);
            string expected = Normalize(stdout);

            string dbPath = GrimoiraCliRunner.InstanceDbPath(instance);
            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = Normalize(new RecallTool().ExecuteCli(connection, "recallclitopic"));

            Assert.Equal(expected, actual);
            Assert.Contains("recallclitopic", actual);
            Assert.DoesNotContain("no chat history matches", actual);
        }
        finally
        {
            GrimoiraCliRunner.DeleteInstance(instance);
            File.Delete(transcript);
        }
    }

    [Fact]
    public void CliShapeReportsNoChatHistoryMatchesForAGapQuery()
    {
        string instance = GrimoiraCliRunner.NewTestInstance("recall-cli-gap");
        try
        {
            GrimoiraCliRunner.Run($"init --instance {instance}");

            (string stdout, int exitCode) = GrimoiraCliRunner.Seed($"recall --instance {instance} nothing-ever-matches-this-term");
            Assert.Equal(0, exitCode);
            string expected = Normalize(stdout);

            string dbPath = GrimoiraCliRunner.InstanceDbPath(instance);
            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = Normalize(new RecallTool().ExecuteCli(connection, "nothing-ever-matches-this-term"));

            Assert.Equal(expected, actual);
            Assert.Contains("0 total chat matches", actual);
        }
        finally
        {
            GrimoiraCliRunner.DeleteInstance(instance);
        }
    }

    // CliTokens (the shared Tokens() used by ExecuteCli) splits on hyphens the same way grimoira.cs's own
    // Tokens does, unlike the MCP path's tokenizer — pins that the CLI shape stays untouched by the
    // MCP-side tokenizer fix. Compared directly against ExecuteCli rather than through the external CLI
    // process: a real confident match's header always carries a literal "…" (the session-id ellipsis,
    // unrelated to this fix), which this machine's console best-fits down to "." only on the redirected
    // external-process path — a pre-existing gap in Normalize()'s mangling compensation (it only
    // compensates "•"), never exercised before because CliShapeMatchesTodaysCliOutputForAConfidentMatch's
    // fixture text is under the 40-char indexing gate and so never actually confidently matches.
    [Fact]
    public void CliShapeFindsAConfidentMatchForAHyphenatedQuery()
    {
        string instance = GrimoiraCliRunner.NewTestInstance("recall-cli-hyphen");
        string transcript = MakeFixtureTranscript("recall-hyphen");
        try
        {
            GrimoiraCliRunner.Run($"init --instance {instance}");
            GrimoiraCliRunner.Seed($"index-chat --instance {instance} --from \"{transcript}\"");

            string dbPath = GrimoiraCliRunner.InstanceDbPath(instance);
            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = new RecallTool().ExecuteCli(connection, "recall-hyphentopic");

            Assert.Contains("recall-hyphentopic", actual);
            Assert.DoesNotContain("no chat history matches", actual);
        }
        finally
        {
            GrimoiraCliRunner.DeleteInstance(instance);
            File.Delete(transcript);
        }
    }

    [Fact]
    public void McpShapeMatchesTodaysMcpOutputForAConfidentMatch()
    {
        string instance = GrimoiraCliRunner.NewTestInstance("recall-mcp-hit");
        string transcript = MakeFixtureTranscript("recallmcp"); // indexed token "recallmcptopic" must equal the query (see the CLI twin above)
        string? previousInstanceEnv = Environment.GetEnvironmentVariable("GRIMOIRA_INSTANCE");
        try
        {
            GrimoiraCliRunner.Run($"init --instance {instance}");
            GrimoiraCliRunner.Seed($"index-chat --instance {instance} --from \"{transcript}\"");

            Environment.SetEnvironmentVariable("GRIMOIRA_INSTANCE", instance);
            string expected = (string)InvokeMcpRecall("recallmcptopic")!;

            string dbPath = GrimoiraCliRunner.InstanceDbPath(instance);
            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = new RecallTool().ExecuteMcp(connection, "recallmcptopic");

            Assert.Equal(expected, actual);
            Assert.Contains("recallmcptopic", actual);
            Assert.DoesNotContain("no chat history matches", actual);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GRIMOIRA_INSTANCE", previousInstanceEnv);
            GrimoiraCliRunner.DeleteInstance(instance);
            File.Delete(transcript);
        }
    }

    [Fact]
    public void McpShapeReportsGapSuffixWhenNoMatch()
    {
        string instance = GrimoiraCliRunner.NewTestInstance("recall-mcp-gap");
        string? previousInstanceEnv = Environment.GetEnvironmentVariable("GRIMOIRA_INSTANCE");
        try
        {
            GrimoiraCliRunner.Run($"init --instance {instance}");

            Environment.SetEnvironmentVariable("GRIMOIRA_INSTANCE", instance);
            string expected = (string)InvokeMcpRecall("nothing-ever-matches-this-mcp-term")!;

            string dbPath = GrimoiraCliRunner.InstanceDbPath(instance);
            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = new RecallTool().ExecuteMcp(connection, "nothing-ever-matches-this-mcp-term");

            Assert.Equal(expected, actual);
            Assert.Contains("gap logged", actual);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GRIMOIRA_INSTANCE", previousInstanceEnv);
            GrimoiraCliRunner.DeleteInstance(instance);
        }
    }

    private static string MakeFixtureTranscript(string session)
    {
        string path = Path.Combine(Path.GetTempPath(), $"grimoira-{session}-{Guid.NewGuid():N}.jsonl");
        string[] lines =
        [
            "{\"type\":\"user\",\"uuid\":\"11111111-1111-1111-1111-111111111111\",\"timestamp\":\"2026-09-25T10:00:00.000Z\",\"message\":{\"content\":\"a fixture message mentioning " + session + "topic, long enough to pass the length gate\"}}",
        ];
        File.WriteAllLines(path, lines);
        return path;
    }

    private static object? InvokeMcpRecall(string query)
    {
        Assembly mcp = Assembly.LoadFrom(FindMcpDll());
        Type tools = mcp.GetType("GrimoiraTools") ?? throw new InvalidOperationException("GrimoiraTools type not found in mcp.dll");
        MethodInfo method = tools.GetMethod("recall", BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException("GrimoiraTools.recall not found in mcp.dll");
        return method.Invoke(null, [query]);
    }

    private static string FindMcpDll()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "bin", "mcp.dll");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new InvalidOperationException(
            $"bin/mcp.dll not found above {AppContext.BaseDirectory} — run build-mcp.ps1 first");
    }

    private static string Normalize(string s) => s.Replace("\r\n", "\n").Trim().Replace('•', '*').Replace('—', '-').Replace("\a", "*");
}
