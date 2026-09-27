using Grimora.Store.Data;
using System.Text.RegularExpressions;
using Xunit;

namespace Grimora.Layout.Tests;

// RESTRUCTURE.md section 1: "The golden MCP list is `tools/list` from today's `mcp.cs` (25 names with
// their parameters). The golden CLI list is the 70 kept verbs." Section 5, versioning: "the 25 MCP
// tool names and their parameter names stay. The 70 kept CLI verbs and their flags stay ... The
// golden-list test fails when a name disappears."
//
// Slice 2 does not move any tool yet (grimora.cs and mcp.cs are still the working host, section 0 rule
// 3), so this test pins the golden lists against the source that exists today. Later slices assert
// the same lists against the registry as tools move.
//
// Deliberate exception, after slice 8 (design checklist "Secrets in outputs"): `redact-chat` is a new
// Grimora.Memory verb, added by the standalone secrets/redaction step, not moved from grimora.cs (grimora.cs
// never had this verb — the CLI list is 71, one more than the 70 RESTRUCTURE.md counted at slice 2).
public partial class GoldenListsTests
{
    // Section 2.1: 5 + 9 + 5 + 6 + 13 + 32 = 70 mapped, 4 dropped (loop, start, tick, selftest).
    // Plus 1: "redact-chat" (new, see the class comment above) = 71.
    public static readonly string[] GoldenCliVerbs =
    [
        // Grimora.Store (5)
        "init", "import", "backup", "stats", "history",
        // Grimora.Facts (9)
        "query", "add", "shed-fact", "todo", "todos", "done", "finding", "findings", "resolve",
        // Grimora.Memory (6, including the new redact-chat)
        "mem", "index-memory", "shed-memory", "index-chat", "recall", "redact-chat",
        // Grimora.Docs (6)
        "doc", "index-docs", "recompact-docs", "shed-doc", "add-synthesis", "shed-synthesis",
        // Grimora.Graph (13)
        "project", "projects", "forget-project", "index-packages", "extract-edges", "candidates",
        "promote", "promote-all", "seed-edges", "impact", "graph-query", "graph-path", "graph-explain",
        // Grimora.Brain (32)
        "brain", "core", "scope", "common", "place", "learn", "learn-batch", "set-hard", "export",
        "audit", "tidy", "why", "merge", "verify", "forget", "unlink", "stale", "distill", "seed",
        "gaps", "node", "triple", "slot", "stage", "flush", "list", "dismiss", "clear",
        "spine-export", "spine-import", "shed-node", "eval",
    ];

    // Section 2.2: 1 + 3 + 3 + 1 + 4 + 10 + 3 = 25 of 25.
    public static readonly string[] GoldenMcpTools =
    [
        "history",
        "fact", "log_finding", "open_findings",
        "rule", "shed_memory", "recall",
        "doc",
        "impact", "graph_query", "graph_path", "graph_explain",
        "brain_core", "brain_scope", "brain_common", "brain_place", "brain_recall", "brain_impact",
        "brain_learn", "brain_gaps", "brain_stage", "brain_flush",
        "idp_token", "workspace_capabilities", "workspace_search",
    ];

    [Fact]
    public void GoldenCliListHas71UniqueVerbs()
    {
        Assert.Equal(71, GoldenCliVerbs.Length);
        Assert.Equal(71, GoldenCliVerbs.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void GoldenMcpListHas25UniqueTools()
    {
        Assert.Equal(25, GoldenMcpTools.Length);
        Assert.Equal(25, GoldenMcpTools.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void EveryTopLevelCliVerbStillExistsInGrimoraCs()
    {
        string source = File.ReadAllText(Path.Combine(RepoPaths.Root, "src", "Grimora.Server", "Data", "CliDispatch.cs"));
        // These are the golden verbs reachable as a top-level `case "<verb>":` in grimora.cs today.
        // The Brain sub-verbs (core, scope, ... eval) live in the nested `brain`/`stage` switches
        // and are checked separately below.
        string[] topLevel =
        [
            "init", "import", "query", "index-chat", "index-packages", "index-docs", "doc",
            "index-memory", "mem", "brain", "shed-doc", "shed-synthesis", "add-synthesis",
            "shed-memory", "shed-fact", "recompact-docs", "recall", "eval", "add", "history",
            "seed-edges", "spine-export", "shed-node", "spine-import", "project", "projects",
            "forget-project", "extract-edges", "candidates", "promote", "promote-all", "impact",
            "graph-query", "graph-path", "graph-explain", "todo", "todos", "done", "finding",
            "findings", "resolve", "stats", "backup", "stage", "flush",
        ];
        foreach (string verb in topLevel)
        {
            Assert.Contains($"case \"{verb}\":", source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void EveryBrainSubVerbStillExistsInGrimoraCs()
    {
        string source = File.ReadAllText(Path.Combine(RepoPaths.Root, "src", "Grimora.Server", "Data", "CliDispatch.cs"));
        string[] brainSubVerbs =
        [
            "core", "scope", "common", "place", "recall", "impact", "learn", "learn-batch",
            "set-hard", "export", "audit", "tidy", "why", "merge", "verify", "forget", "unlink",
            "stale", "distill", "seed", "stats", "gaps",
        ];
        foreach (string verb in brainSubVerbs)
        {
            Assert.Contains($"case \"{verb}\":", source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Every25McpToolStillExistsInMcpCs()
    {
        string source = File.ReadAllText(Path.Combine(RepoPaths.Root, "mcp.cs"));
        // Descriptions carry arbitrary parentheses, so this walks forward from each attribute to the
        // next `public static` signature instead of matching the attribute block with one regex.
        List<string> found = [];
        Regex signature = ToolMethodSignature();
        int index = 0;
        while ((index = source.IndexOf("[McpServerTool]", index, StringComparison.Ordinal)) >= 0)
        {
            Match m = signature.Match(source, index);
            Assert.True(m.Success, $"no method signature found after [McpServerTool] at offset {index}");
            found.Add(m.Groups[1].Value);
            index = m.Index + m.Length;
        }

        Assert.Equal(25, found.Count);
        foreach (string tool in GoldenMcpTools)
        {
            Assert.Contains(tool, found);
        }
    }

    [GeneratedRegex(@"public static (?:async )?(?:Task<string>|string) (\w+)\s*\(", RegexOptions.None, RegexTimeout.Milliseconds)]
    private static partial Regex ToolMethodSignature();
}
