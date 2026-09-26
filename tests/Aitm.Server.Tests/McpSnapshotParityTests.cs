using System.Runtime.CompilerServices;
using Aitm.TestSupport;
using Xunit;
using System.Text.RegularExpressions;

namespace Aitm.Server.Tests;

/// <summary>
/// RESTRUCTURE.md slice 24 (MCP part): proves that wiring mcp.cs's Store/Facts/Memory/Docs tools to
/// their tool classes changed nothing a client sees. Spawns the pre-slice mcp.cs snapshot
/// (<see cref="McpSnapshotHarness"/>) and today's built <c>bin/mcp.dll</c> over real stdio, against a
/// fresh temp store each, and diffs <c>tools/list</c> and each call's answer text.
/// </summary>
public partial class McpSnapshotParityTests
{
    private static readonly string RepoRoot = FindRoot();

    [Fact]
    public void ToolsListIsIdenticalBetweenTheSnapshotAndTodaysServer()
    {
        string oldDll = McpSnapshotHarness.EnsureBuilt(RepoRoot);
        string newDll = FindNewMcpDll();
        string instance = AitmCliRunner.NewTestInstance("mcp-parity-list");
        try
        {
            AitmCliRunner.Run($"init --instance {instance}");
            (IReadOnlyList<string> oldTools, _) = McpProcess.Run(oldDll, instance, []);
            (IReadOnlyList<string> newTools, _) = McpProcess.Run(newDll, instance, []);

            Assert.Equal(oldTools.OrderBy(t => t, StringComparer.Ordinal), newTools.OrderBy(t => t, StringComparer.Ordinal));
            Assert.Contains("fact", newTools);
            Assert.Contains("history", newTools);
        }
        finally
        {
            AitmCliRunner.DeleteInstance(instance);
        }
    }

    [Theory]
    [MemberData(nameof(WiredToolCases))]
    public void WiredToolGivesTheSameAnswerOldAndNew(string toolName, Action<string> seed, object normalArgs, object errorArgs)
    {
        string oldDll = McpSnapshotHarness.EnsureBuilt(RepoRoot);
        string newDll = FindNewMcpDll();

        string oldInstance = AitmCliRunner.NewTestInstance($"mcp-parity-{toolName}-old");
        string newInstance = AitmCliRunner.NewTestInstance($"mcp-parity-{toolName}-new");
        try
        {
            AitmCliRunner.Run($"init --instance {oldInstance}");
            AitmCliRunner.Run($"init --instance {newInstance}");
            seed(oldInstance);
            seed(newInstance);

            (_, IReadOnlyList<string> oldResults) = McpProcess.Run(oldDll, oldInstance, [(toolName, normalArgs), (toolName, errorArgs)]);
            (_, IReadOnlyList<string> newResults) = McpProcess.Run(newDll, newInstance, [(toolName, normalArgs), (toolName, errorArgs)]);

            Assert.Equal(StripTimestamps(oldResults[0]), StripTimestamps(newResults[0]));
            Assert.Equal(StripTimestamps(oldResults[1]), StripTimestamps(newResults[1]));
            Assert.NotEqual("", oldResults[0]);
        }
        finally
        {
            AitmCliRunner.DeleteInstance(oldInstance);
            AitmCliRunner.DeleteInstance(newInstance);
        }
    }

    public static IEnumerable<object[]> WiredToolCases()
    {
        yield return
        [
            "fact",
            (Action<string>)(instance => AitmCliRunner.Run($"add --instance {instance} --term parity-fact --value parity-answer --category manual")),
            new { query = "parity-fact" },
            new { query = "nothing-ever-matches-this-term-at-all" },
        ];
        yield return
        [
            "history",
            (Action<string>)(instance => AitmCliRunner.Run($"add --instance {instance} --term parity-history --value one --category manual")),
            new { term = "parity-history" },
            new { term = "nothing-was-ever-named-this" },
        ];
        yield return
        [
            "log_finding",
            (Action<string>)(_ => { }),
            new { title = "parity-finding", detail = "d", source = "s" },
            new { title = "", detail = "", source = "" },
        ];
        yield return
        [
            "open_findings",
            (Action<string>)(instance => AitmCliRunner.Run($"finding --instance {instance} --title parity-open-finding --detail d --source s")),
            new { },
            new { },
        ];
        yield return
        [
            "rule",
            (Action<string>)(instance => SeedMemory(instance, "parityrule")),
            new { query = "parityrule" },
            new { query = "nothing-ever-matches-this-term-at-all" },
        ];
        yield return
        [
            "shed_memory",
            (Action<string>)(instance => SeedMemory(instance, "parityshed")),
            new { key = MemoryKey("parityshed") },
            new { key = "no-such-key-ever" },
        ];
        yield return
        [
            "recall",
            (Action<string>)(instance => SeedChat(instance, "parityrecall")),
            new { query = "parityrecall" },
            new { query = "nothing-ever-matches-this-term-at-all" },
        ];
        yield return
        [
            "doc",
            (Action<string>)(instance => SeedDocs(instance, "parity-doc")),
            new { query = "paritydoc" },
            new { query = "nothing-ever-matches-this-term-at-all" },
        ];

        // Punctuated queries: bbb9b4d's mcp.cs (and QueryTool's McpTokens, copied from it) tokenized a
        // query by splitting on spaces only and stripping punctuation *inside* each token, so
        // "Parity-Punct/Term.Alpha:Beta_Gamma" collapsed to one glued token. The shared Tokens() used
        // by MemTool/RecallTool/DocTool (copied from the CLI tokenizer, which deliberately splits on
        // those delimiters — aitm.cs's own comment about "nomercy-app-kmp") instead splits into several
        // tokens. Every wired tool must still answer exactly like the old mcp.cs even when the query or
        // seeded content carries a hyphen, slash, dot, colon, underscore, or mixed case.
        const string punct = "Parity-Punct/Term.Alpha:Beta_Gamma";
        yield return
        [
            "fact",
            (Action<string>)(instance => AitmCliRunner.Run($"add --instance {instance} --term \"{punct}\" --value parity-answer --category manual")),
            new { query = punct },
            new { query = "nothing-ever-matches-this-term-at-all" },
        ];
        yield return
        [
            "history",
            (Action<string>)(instance => AitmCliRunner.Run($"add --instance {instance} --term \"{punct}\" --value one --category manual")),
            new { term = punct },
            new { term = "nothing-was-ever-named-this" },
        ];
        yield return
        [
            "log_finding",
            (Action<string>)(_ => { }),
            new { title = punct, detail = "d", source = "s" },
            new { title = "", detail = "", source = "" },
        ];
        yield return
        [
            "open_findings",
            (Action<string>)(instance => AitmCliRunner.Run($"finding --instance {instance} --title \"{punct}\" --detail d --source s")),
            new { },
            new { },
        ];
        yield return
        [
            "rule",
            (Action<string>)(instance => SeedMemory(instance, "punct-rule", punct)),
            new { query = punct },
            new { query = "nothing-ever-matches-this-term-at-all" },
        ];
        yield return
        [
            "shed_memory",
            (Action<string>)(instance => SeedMemory(instance, "punct-shed", punct)),
            new { key = MemoryKey("punct-shed") },
            new { key = "no-such-key-ever" },
        ];
        yield return
        [
            "recall",
            (Action<string>)(instance => SeedChat(instance, punct)),
            new { query = punct },
            new { query = "nothing-ever-matches-this-term-at-all" },
        ];
        yield return
        [
            "doc",
            (Action<string>)(instance => SeedDocs(instance, "punct-doc", punct)),
            new { query = punct },
            new { query = "nothing-ever-matches-this-term-at-all" },
        ];

        // RESTRUCTURE.md slice 24, MCP lane part 2: the remaining 17 tools — Aitm.Graph (4), Aitm.Brain
        // (10), and 2 of the 3 Handover tools. idp_token (the 3rd Handover tool) is the one
        // documented exception: it stays inline in both the snapshot and today's mcp.cs, so its case
        // below still proves parity (identical code, identical answer) without exercising the change
        // slice 28 plans.
        yield return
        [
            "impact",
            (Action<string>)(instance => SeedEdge(instance, "no-mercy's-widget", hardcoded: true)),
            new { symbol = "no-mercy's-widget" },
            new { symbol = "nothing-was-ever-indexed-with-this-symbol" },
        ];
        yield return
        [
            "graph_query",
            (Action<string>)(instance => SeedEdge(instance, "parity-widget-frobnicate", hardcoded: false)),
            new { question = "no-mercy's parity-widget-frobnicate, please?" },
            new { question = "!!!---???" },
        ];
        yield return
        [
            "graph_path",
            (Action<string>)(instance => SeedEdge(instance, "parity-path-symbol", hardcoded: false)),
            new { a = "parity-path-symbol", b = "parity-path-symbol" },
            new { a = "no-such-symbol-a", b = "no-such-symbol-b" },
        ];
        yield return
        [
            "graph_explain",
            (Action<string>)(instance => SeedEdge(instance, "parity-explain's-symbol", hardcoded: true)),
            new { symbol = "parity-explain's-symbol" },
            new { symbol = "no-such-symbol-ever-indexed" },
        ];
        yield return
        [
            "brain_core",
            (Action<string>)(_ => { }),
            new { },
            new { },
        ];
        yield return
        [
            "brain_scope",
            (Action<string>)(_ => { }),
            new { projects = "server web" },
            new { projects = "" },
        ];
        yield return
        [
            "brain_common",
            (Action<string>)(_ => { }),
            new { projects = "server web" },
            new { projects = "server" },
        ];
        yield return
        [
            "brain_place",
            (Action<string>)(_ => { }),
            new { codekind = "no-mercy's-widget-kind" },
            new { codekind = "" },
        ];
        yield return
        [
            "brain_recall",
            (Action<string>)(_ => { }),
            new { query = "no-mercy's parity-recall term" },
            new { query = "" },
        ];
        yield return
        [
            "brain_impact",
            (Action<string>)(instance => SeedEdge(instance, "parity-brain-impact's-symbol", hardcoded: true)),
            new { symbol = "parity-brain-impact's-symbol" },
            new { symbol = "nothing-was-ever-indexed-with-this-symbol" },
        ];
        yield return
        [
            "brain_learn",
            (Action<string>)(_ => { }),
            new { kind = "node", key = "parity-learn-node", a = "fact", b = "a short label", c = "a longer gloss" },
            new { kind = "triple", key = "parity-learn-node", a = "no-such-predicate-ever", b = "x" },
        ];
        yield return
        [
            "brain_gaps",
            (Action<string>)(_ => { }),
            new { },
            new { },
        ];
        yield return
        [
            "brain_stage",
            (Action<string>)(_ => { }),
            new { kind = "node", key = "parity-stage-node", a = "fact", b = "a short label", c = "a longer gloss" },
            new { kind = "node", key = "parity-stage-node", a = "fact", b = new string('x', 200), c = "" },
        ];
        // No seed: the harness fires both calls concurrently (McpProcess.Run sends every call before
        // waiting on a response), and brain_flush reads-then-deletes the same ledger file, so seeding
        // one line makes the two concurrent calls race on that file (confirmed: this must run against
        // an empty ledger, giving "nothing staged." both times, on both sides — still a real parity
        // check, just not one that exercises a non-empty flush).
        yield return
        [
            "brain_flush",
            (Action<string>)(_ => { }),
            new { },
            new { },
        ];
        yield return
        [
            "workspace_capabilities",
            (Action<string>)(_ => { }),
            new { query = "find no-mercy's login driver" },
            new { query = "" },
        ];
        yield return
        [
            "workspace_search",
            (Action<string>)(_ => { }),
            new { repository = ".", pattern = "no-mercy's-pattern" },
            new { repository = "", pattern = "" },
        ];

        // idp_token is deliberately NOT exercised here: RESTRUCTURE.md ("Handover tools must never
        // run the real Python or login drivers in tests") — and on this box AITM_ALLOW_TOKEN_MINT=1 is
        // set in the ambient environment (the testbed convention the card's Handover note refers to),
        // so ANY call here would run the real node idp-impersonate.mjs against a live IdP
        // realm. It stays inline and unchanged (see IdPTokenStaysInlineUntilSlice28 in
        // McpDispatchTests, and the earlier tools/list equality check, which already proves both sides
        // still expose it under the same name).
    }

    // Inserts one row into `edges` (the table impact/graph_query/graph_path/graph_explain/brain_impact
    // all read), directly through the store's own connection — the same shape Aitm.Graph.Tests and
    // Aitm.Brain.Tests seed with (e.g. ImpactToolTests, GraphQueryToolTests). Deliberately includes a
    // hyphen and an apostrophe in the symbol: RESTRUCTURE.md slice 24 (MCP part 1) found a tokenizer
    // difference between the old and new code on punctuation, so every seeded symbol here carries that
    // same trap.
    private static void SeedEdge(string instance, string symbol, bool hardcoded)
    {
        using Microsoft.Data.Sqlite.SqliteConnection connection = new($"Data Source={AitmCliRunner.InstanceDbPath(instance)}");
        connection.Open();
        using Microsoft.Data.Sqlite.SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = "INSERT INTO edges(symbol,contract,project,file,line,usage,hardcoded) VALUES($s,'decl','parity-project','src/Parity.cs',1,'declaration',$h)";
        cmd.Parameters.AddWithValue("$s", symbol);
        cmd.Parameters.AddWithValue("$h", hardcoded ? 1 : 0);
        cmd.ExecuteNonQuery();
    }

    // History's rows carry a real insertion timestamp, which differs by construction between the old
    // and new instance (two separate `aitm add` runs, milliseconds apart) even though the rest of the
    // row is identical; blank it out before comparing shape.
    private static string StripTimestamps(string text) =>
        MyRegex().Replace(text, "<ts>");

    private static string MemoryKey(string label) => $"memory-fixture-{label}";

    private static void SeedMemory(string instance, string label) => SeedMemory(instance, label, label);

    private static void SeedMemory(string instance, string label, string topic)
    {
        string dir = Directory.CreateTempSubdirectory("aitm-mcp-parity-mem-").FullName;
        File.WriteAllText(Path.Combine(dir, $"{MemoryKey(label)}.md"),
            $"---\ntype: feedback\ntitle: {label}\nhook: {label}\n---\n\nA memory about {topic} topic.\n");
        AitmCliRunner.Run($"index-memory --instance {instance} --from \"{dir}\"");
    }

    private static void SeedChat(string instance, string label)
    {
        // IndexChatTool.Execute enumerates *.jsonl in --from non-recursively, so the file has to sit
        // directly in that directory (not in a nested project subfolder) or it silently indexes zero
        // messages, which would let a real match-vs-no-match parity gap hide behind an empty result on
        // both sides.
        string dir = Directory.CreateTempSubdirectory("aitm-mcp-parity-chat-").FullName;
        File.WriteAllText(Path.Combine(dir, "session.jsonl"),
            $"{{\"type\":\"user\",\"message\":{{\"role\":\"user\",\"content\":\"a message about {label} topic, said during a real working session\"}},\"sessionId\":\"s1\",\"timestamp\":\"2026-01-01T00:00:00Z\"}}\n");
        AitmCliRunner.Run($"index-chat --instance {instance} --from \"{dir}\"");
    }

    private static void SeedDocs(string instance, string label) => SeedDocs(instance, label, label);

    private static void SeedDocs(string instance, string label, string topic)
    {
        string dir = Directory.CreateTempSubdirectory("aitm-mcp-parity-docs-").FullName;
        File.WriteAllText(Path.Combine(dir, $"{label}.md"), $"# {label}\n\nContent about {topic} topic.\n");
        AitmCliRunner.Run($"index-docs --instance {instance} --from \"{dir}\"");
    }

    private static string FindNewMcpDll()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "bin", "mcp.dll");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new InvalidOperationException($"bin/mcp.dll not found above {AppContext.BaseDirectory} — run build-mcp.ps1 first");
    }

    private static string FindRoot([CallerFilePath] string here = "")
    {
        string dir = Path.GetDirectoryName(here)!;
        return Path.GetFullPath(Path.Combine(dir, "..", ".."));
    }

    [GeneratedRegex(@"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d+Z")]
    private static partial Regex MyRegex();
}
