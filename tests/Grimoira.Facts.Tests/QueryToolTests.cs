using System.Reflection;
using Grimoira.TestSupport;
using Grimoira.Facts.Tools;
using Grimoira.Store.Data;
using Microsoft.Data.Sqlite;
using Xunit;
using System.Text.RegularExpressions;

namespace Grimoira.Facts.Tests;

public partial class QueryToolTests
{
    [Fact]
    public void CliShapeMatchesTodaysCliOutputForAConfidentMatch()
    {
        string instance = GrimoiraCliRunner.NewTestInstance("query-cli-hit");
        try
        {
            GrimoiraCliRunner.Run($"init --instance {instance}");
            GrimoiraCliRunner.Run($"add --instance {instance} --term query-fixture --value the-answer --category manual");

            (string stdout, int exitCode) = GrimoiraCliRunner.Run($"query --instance {instance} query-fixture");
            Assert.Equal(0, exitCode);
            string expected = Normalize(stdout);

            string dbPath = GrimoiraCliRunner.InstanceDbPath(instance);
            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = Normalize(new QueryTool(new UsageSignal()).ExecuteCli(connection, "query-fixture"));

            Assert.Equal(StripTiming(expected), StripTiming(actual));
            Assert.Contains("the-answer", actual);
        }
        finally
        {
            GrimoiraCliRunner.DeleteInstance(instance);
        }
    }

    [Fact]
    public void CliShapeReportsNoConfidentAnswerForAGapQuery()
    {
        string instance = GrimoiraCliRunner.NewTestInstance("query-cli-gap");
        try
        {
            GrimoiraCliRunner.Run($"init --instance {instance}");

            (string stdout, int exitCode) = GrimoiraCliRunner.Run($"query --instance {instance} nothing-ever-matches-this-term");
            Assert.Equal(0, exitCode);
            string expected = Normalize(stdout);

            string dbPath = GrimoiraCliRunner.InstanceDbPath(instance);
            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = Normalize(new QueryTool(new UsageSignal()).ExecuteCli(connection, "nothing-ever-matches-this-term"));

            Assert.Equal(StripTiming(expected), StripTiming(actual));
            Assert.Contains("gap logged", actual);
        }
        finally
        {
            GrimoiraCliRunner.DeleteInstance(instance);
        }
    }

    // mcp.cs's `fact` resolves its own connection from GRIMOIRA_INSTANCE (same pattern as HistoryToolTests'
    // McpShapeMatchesTodaysMcpOutput).
    [Fact]
    public void McpShapeMatchesTodaysMcpOutputForAConfidentMatch()
    {
        string instance = GrimoiraCliRunner.NewTestInstance("query-mcp-hit");
        string? previousInstanceEnv = Environment.GetEnvironmentVariable("GRIMOIRA_INSTANCE");
        try
        {
            GrimoiraCliRunner.Run($"init --instance {instance}");
            GrimoiraCliRunner.Run($"add --instance {instance} --term queryfixturemcp --value the-mcp-answer --category manual");

            Environment.SetEnvironmentVariable("GRIMOIRA_INSTANCE", instance);
            string expected = CliGoldens.Frozen("fact " + "queryfixturemcp", () => (string)InvokeMcpFact("queryfixturemcp")!);

            string dbPath = GrimoiraCliRunner.InstanceDbPath(instance);
            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = new QueryTool(new UsageSignal()).ExecuteMcp(connection, "queryfixturemcp");

            Assert.Equal(expected, actual);
            Assert.Contains("the-mcp-answer", actual);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GRIMOIRA_INSTANCE", previousInstanceEnv);
            GrimoiraCliRunner.DeleteInstance(instance);
        }
    }

    [Fact]
    public void McpShapeReportsGapSuffixWhenNoMatch()
    {
        string instance = GrimoiraCliRunner.NewTestInstance("query-mcp-gap");
        string? previousInstanceEnv = Environment.GetEnvironmentVariable("GRIMOIRA_INSTANCE");
        try
        {
            GrimoiraCliRunner.Run($"init --instance {instance}");

            Environment.SetEnvironmentVariable("GRIMOIRA_INSTANCE", instance);
            string expected = CliGoldens.Frozen("fact " + "nothing-ever-matches-this-mcp-term", () => (string)InvokeMcpFact("nothing-ever-matches-this-mcp-term")!);

            string dbPath = GrimoiraCliRunner.InstanceDbPath(instance);
            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = new QueryTool(new UsageSignal()).ExecuteMcp(connection, "nothing-ever-matches-this-mcp-term");

            Assert.Equal(expected, actual);
            Assert.Contains("gap logged", actual);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GRIMOIRA_INSTANCE", previousInstanceEnv);
            GrimoiraCliRunner.DeleteInstance(instance);
        }
    }

    // RESTRUCTURE.md slice 24 bullet 1: ports the `selftest` checks QueryToolTests didn't yet cover
    // (grimoira.cs's old SelfTest: "floor: distinctive term still retrieves in a populated corpus" / "floor:
    // generic low-IDF term is refused once corpus is large" / "coverage: query whose tokens mostly match
    // retrieves" / "coverage: 3+ token query matching one tangential word is refused") now that selftest
    // itself is gone.
    [Fact]
    public void ADistinctiveTermStillRetrievesButAGenericLowIdfTermIsRefusedInAPopulatedCorpus()
    {
        string instance = GrimoiraCliRunner.NewTestInstance("query-floor-gate");
        try
        {
            GrimoiraCliRunner.Run($"init --instance {instance}");
            for (int n = 0; n < 22; n++)
                GrimoiraCliRunner.Run($"add --instance {instance} --term \"filler concept {n}\" --category misc --value \"value number {n} alpha\"");
            GrimoiraCliRunner.Run($"add --instance {instance} --term \"spritevtt muxer crash\" --category bug --value \"spritevtt eagain in multi output pipeline\"");

            string dbPath = GrimoiraCliRunner.InstanceDbPath(instance);
            using SqliteConnection connection = StoreConnection.Open(dbPath);
            QueryTool tool = new(new UsageSignal());

            string distinctive = tool.ExecuteCli(connection, "spritevtt");
            Assert.Contains("spritevtt", distinctive);
            Assert.DoesNotContain("no confident answer", distinctive);

            string generic = tool.ExecuteCli(connection, "value");
            Assert.Contains("no confident answer", generic);
        }
        finally
        {
            GrimoiraCliRunner.DeleteInstance(instance);
        }
    }

    [Fact]
    public void ACoverageQueryMostlyMatchingRetrievesButOneTangentialWordIsRefused()
    {
        string instance = GrimoiraCliRunner.NewTestInstance("query-coverage-gate");
        try
        {
            GrimoiraCliRunner.Run($"init --instance {instance}");
            GrimoiraCliRunner.Run($"add --instance {instance} --term \"spritevtt muxer crash\" --category bug --value \"spritevtt eagain in multi output pipeline\"");

            string dbPath = GrimoiraCliRunner.InstanceDbPath(instance);
            using SqliteConnection connection = StoreConnection.Open(dbPath);
            QueryTool tool = new(new UsageSignal());

            string mostlyMatching = tool.ExecuteCli(connection, "spritevtt muxer crash");
            Assert.DoesNotContain("no confident answer", mostlyMatching);

            string tangential = tool.ExecuteCli(connection, "spritevtt rabbit telescope");
            Assert.Contains("no confident answer", tangential);
        }
        finally
        {
            GrimoiraCliRunner.DeleteInstance(instance);
        }
    }

    // RESTRUCTURE.md slice 24 bullet 1: ports the `selftest` checks "isolation: chat never leaks into the
    // verified-facts channel" and "memory: isolated from the verified-facts channel" now that selftest
    // itself is gone. Facts, chat and memory are separate tables with separate FTS mirrors, so a facts
    // query must never surface a row that only exists in chat or memory.
    [Fact]
    public void AFactsQueryNeverSurfacesAChatOnlyRow()
    {
        string instance = GrimoiraCliRunner.NewTestInstance("query-isolation-chat");
        try
        {
            GrimoiraCliRunner.Run($"init --instance {instance}");
            string dbPath = GrimoiraCliRunner.InstanceDbPath(instance);
            using (SqliteConnection setup = StoreConnection.Open(dbPath))
            {
                using SqliteCommand insertChat = setup.CreateCommand();
                insertChat.CommandText = "INSERT INTO chat(k,session,ts,role,text) VALUES('s:1','s','t','user',$tx)";
                insertChat.Parameters.AddWithValue("$tx", "the operator said never use optionaldependencies in package json");
                insertChat.ExecuteNonQuery();
                using SqliteCommand insertChatFts = setup.CreateCommand();
                insertChatFts.CommandText = "INSERT INTO chat_fts(k,text) VALUES('s:1',$tx)";
                insertChatFts.Parameters.AddWithValue("$tx", "the operator said never use optionaldependencies in package json");
                insertChatFts.ExecuteNonQuery();
            }

            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string result = new QueryTool(new UsageSignal()).ExecuteCli(connection, "optionaldependencies");

            Assert.Contains("no confident answer", result);
        }
        finally
        {
            GrimoiraCliRunner.DeleteInstance(instance);
        }
    }

    [Fact]
    public void AFactsQueryNeverSurfacesAMemoryOnlyRow()
    {
        string instance = GrimoiraCliRunner.NewTestInstance("query-isolation-memory");
        try
        {
            GrimoiraCliRunner.Run($"init --instance {instance}");
            string dbPath = GrimoiraCliRunner.InstanceDbPath(instance);
            using (SqliteConnection setup = StoreConnection.Open(dbPath))
            {
                using SqliteCommand insertMemory = setup.CreateCommand();
                insertMemory.CommandText = "INSERT INTO memory(k,type,title,hook,body,links,hard) VALUES('no-useless-comments','feedback','No useless comments','default to zero comments','comments must earn their place','',1)";
                insertMemory.ExecuteNonQuery();
                using SqliteCommand insertMemoryFts = setup.CreateCommand();
                insertMemoryFts.CommandText = "INSERT INTO memory_fts(k,title,hook,body) VALUES('no-useless-comments','No useless comments','default to zero comments','comments must earn their place')";
                insertMemoryFts.ExecuteNonQuery();
            }

            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string result = new QueryTool(new UsageSignal()).ExecuteCli(connection, "comments");

            Assert.Contains("no confident answer", result);
        }
        finally
        {
            GrimoiraCliRunner.DeleteInstance(instance);
        }
    }

    // RESTRUCTURE.md slice 24 bullet 1: ports the `selftest` checks that exercised the query tokenizer's
    // splitting rules (grimoira.cs's old SelfTest: "tokens: a kebab-case name splits into its words" /
    // "tokens: a path splits on separators") now that selftest itself is gone. Proven end-to-end: a fact
    // whose only mention of a hyphenated or path-shaped term is glued together must still be found by a
    // query written in plain separated words, because the same splitter tokenizes both sides.
    [Fact]
    public void AKebabCaseTermIsRetrievableByItsSeparateWords()
    {
        string instance = GrimoiraCliRunner.NewTestInstance("query-tokens-kebab-case");
        try
        {
            GrimoiraCliRunner.Run($"init --instance {instance}");
            GrimoiraCliRunner.Run($"add --instance {instance} --term the-effortless-encoder --category manual --value \"a report about the effortless encoder\"");

            string dbPath = GrimoiraCliRunner.InstanceDbPath(instance);
            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string result = new QueryTool(new UsageSignal()).ExecuteCli(connection, "effortless encoder");

            Assert.DoesNotContain("no confident answer", result);
            Assert.Contains("the-effortless-encoder", result);
        }
        finally
        {
            GrimoiraCliRunner.DeleteInstance(instance);
        }
    }

    [Fact]
    public void APathShapedTermIsRetrievableBySeparatedWords()
    {
        string instance = GrimoiraCliRunner.NewTestInstance("query-tokens-path");
        try
        {
            GrimoiraCliRunner.Run($"init --instance {instance}");
            GrimoiraCliRunner.Run($"add --instance {instance} --term nomercy-app-web --category manual --value \"apps/nomercy-app-web/src is the web app root\"");

            string dbPath = GrimoiraCliRunner.InstanceDbPath(instance);
            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string result = new QueryTool(new UsageSignal()).ExecuteCli(connection, "nomercy web");

            Assert.DoesNotContain("no confident answer", result);
            Assert.Contains("nomercy-app-web", result);
        }
        finally
        {
            GrimoiraCliRunner.DeleteInstance(instance);
        }
    }

    // RESTRUCTURE.md slice 24 bullet 1: ports the `selftest` check "current: projection reflects latest
    // value only" now that selftest itself is gone. Re-adding the same term with a new value must upsert
    // the CURRENT projection facts_fts/facts read from — the cold mutation history (both values) is a
    // separate concern HistoryToolTests already pins.
    [Fact]
    public void ReAddingTheSameTermUpsertsTheCurrentProjectionToTheLatestValue()
    {
        string instance = GrimoiraCliRunner.NewTestInstance("query-current-projection");
        try
        {
            GrimoiraCliRunner.Run($"init --instance {instance}");
            GrimoiraCliRunner.Run($"add --instance {instance} --term projection-fixture --category config --value 7626");
            GrimoiraCliRunner.Run($"add --instance {instance} --term projection-fixture --category config --value 9999");

            string dbPath = GrimoiraCliRunner.InstanceDbPath(instance);
            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string result = new QueryTool(new UsageSignal()).ExecuteCli(connection, "projection-fixture");

            Assert.Contains("9999", result);
            Assert.DoesNotContain("7626", result);
        }
        finally
        {
            GrimoiraCliRunner.DeleteInstance(instance);
        }
    }

    private static object? InvokeMcpFact(string query)
    {
        Assembly mcp = Assembly.LoadFrom(FindMcpDll());
        Type tools = mcp.GetType("GrimoiraTools") ?? throw new InvalidOperationException("GrimoiraTools type not found in mcp.dll");
        MethodInfo method = tools.GetMethod("fact", BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException("GrimoiraTools.fact not found in mcp.dll");
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

    // Windows redirects the child's stdout through the OEM codepage, not UTF-8, so grimoira.cs's "•" and
    // "—" (neither representable in that codepage) arrive corrupted regardless of the encoding this
    // side decodes with — a capture artifact, not a behaviour difference (mcp.cs's in-process oracle
    // above needs no such workaround). Both sides get the same substitution before comparing shape.
    private static string Normalize(string s) => s.Replace("\r\n", "\n").Trim().Replace('•', '*').Replace('—', '-').Replace("\a", "*");

    // The oracle and the new tool run at slightly different times, so the "(N.NNms)" tail never
    // matches byte for byte; strip it the same way ImportToolTests strips the trailing db path.
    private static string StripTiming(string s) => TrailingParenthesisedTimingMs().Replace(s, "").TrimEnd();

    [GeneratedRegex(@"\(\d+[.,]\d+ms\)\s*$", RegexOptions.None, RegexTimeout.Milliseconds)]
    private static partial Regex TrailingParenthesisedTimingMs();
}
