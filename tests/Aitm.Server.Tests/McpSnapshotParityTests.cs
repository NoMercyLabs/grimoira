using System.Runtime.CompilerServices;
using Aitm.TestSupport;
using Xunit;

namespace Aitm.Server.Tests;

/// <summary>
/// RESTRUCTURE.md slice 24 (MCP part): proves that wiring mcp.cs's Store/Facts/Memory/Docs tools to
/// their tool classes changed nothing a client sees. Spawns the pre-slice mcp.cs snapshot
/// (<see cref="McpSnapshotHarness"/>) and today's built <c>bin/mcp.dll</c> over real stdio, against a
/// fresh temp store each, and diffs <c>tools/list</c> and each call's answer text.
/// </summary>
public class McpSnapshotParityTests
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
    }

    // History's rows carry a real insertion timestamp, which differs by construction between the old
    // and new instance (two separate `aitm add` runs, milliseconds apart) even though the rest of the
    // row is identical; blank it out before comparing shape.
    private static string StripTimestamps(string text) =>
        System.Text.RegularExpressions.Regex.Replace(text, @"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d+Z", "<ts>");

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
}
