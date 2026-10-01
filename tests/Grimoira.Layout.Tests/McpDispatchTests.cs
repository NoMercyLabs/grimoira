using Grimoira.Store.Data;
using System.Text.RegularExpressions;
using Xunit;

namespace Grimoira.Layout.Tests;

// RESTRUCTURE.md slice 24: "grimoira.cs and mcp.cs now hold only dispatch to the registry." This part
// wires the Grimoira.Store/Facts/Memory/Docs tools first (the MCP side). Each listed [McpServerTool]
// method in mcp.cs must call its tool class's ExecuteMcp instead of carrying its own inline copy of
// the logic — a mechanical grep on the source, not a behavioural test (the per-tool oracle tests in
// Grimoira.Store.Tests/Grimoira.Facts.Tests/Grimoira.Memory.Tests/Grimoira.Docs.Tests cover behaviour).
public partial class McpDispatchTests
{
    // MCP tool name -> the tool class whose ExecuteMcp it must call (RESTRUCTURE.md section 2.2,
    // "Grimoira.Store (1)", "Grimoira.Facts (3)", "Grimoira.Memory (3)", "Grimoira.Docs (1)").
    public static readonly (string McpName, string ToolClass)[] StoreFactsMemoryDocsTools =
    [
        ("history", "HistoryTool"),
        ("fact", "QueryTool"),
        ("log_finding", "FindingTool"),
        ("open_findings", "FindingsTool"),
        ("rule", "MemTool"),
        ("shed_memory", "ShedMemoryTool"),
        ("recall", "RecallTool"),
        ("doc", "DocTool"),
    ];

    public static IEnumerable<object[]> StoreFactsMemoryDocsToolsData() =>
        StoreFactsMemoryDocsTools.Select(t => new object[] { t.McpName, t.ToolClass });

    [Theory]
    [MemberData(nameof(StoreFactsMemoryDocsToolsData))]
    public void McpMethodCallsItsToolClass(string mcpName, string toolClass)
    {
        string source = File.ReadAllText(Path.Combine(RepoPaths.Root, "mcp.cs"));
        string body = MethodBody(source, mcpName);
        Assert.True(
            Regex.IsMatch(body, $@"new\s+{Regex.Escape(toolClass)}\s*\(.*\)\s*\.\s*ExecuteMcp\s*\(", RegexOptions.Singleline, RegexTimeout.Span),
            $"mcp.cs's {mcpName}() must call new {toolClass}(...).ExecuteMcp(...) instead of holding its own inline logic.\n---\n{body}");
    }

    // RESTRUCTURE.md slice 24, MCP lane part 2: the remaining 17 tools — Grimoira.Graph (4), Grimoira.Brain
    // (10, the golden list), and 2 of the 3 Handover tools (RESTRUCTURE.md section 2.2). These call
    // ExecuteMcp on a store-backed tool, exactly like StoreFactsMemoryDocsTools above.
    public static readonly (string McpName, string ToolClass)[] GraphAndBrainTools =
    [
        ("impact", "ImpactTool"),
        ("graph_query", "GraphQueryTool"),
        ("graph_path", "GraphPathTool"),
        ("graph_explain", "GraphExplainTool"),
        ("brain_core", "BrainCoreTool"),
        ("brain_scope", "BrainScopeTool"),
        ("brain_common", "BrainCommonTool"),
        ("brain_place", "BrainPlaceTool"),
        ("brain_recall", "BrainRecallTool"),
        ("brain_impact", "BrainImpactTool"),
        ("brain_learn", "BrainLearnTool"),
        ("brain_gaps", "BrainGapsTool"),
        ("brain_stage", "BrainStageTool"),
        ("brain_flush", "BrainFlushTool"),
    ];

    public static IEnumerable<object[]> GraphAndBrainToolsData() =>
        GraphAndBrainTools.Select(t => new object[] { t.McpName, t.ToolClass });

    [Theory]
    [MemberData(nameof(GraphAndBrainToolsData))]
    public void McpMethodCallsItsToolClassExecuteMcp(string mcpName, string toolClass)
    {
        string source = File.ReadAllText(Path.Combine(RepoPaths.Root, "mcp.cs"));
        string body = MethodBody(source, mcpName);
        Assert.True(
            Regex.IsMatch(body, $@"new\s+{Regex.Escape(toolClass)}\s*\(.*\)\s*\.\s*ExecuteMcp\s*\(", RegexOptions.Singleline, RegexTimeout.Span),
            $"mcp.cs's {mcpName}() must call new {toolClass}(...).ExecuteMcp(...) instead of holding its own inline logic.\n---\n{body}");
    }

    // The 2 remaining Handover tools hold no project store (RESTRUCTURE.md "Handover tools ... take
    // server-level dependencies ... instead of a connection"), so they call Execute, not ExecuteMcp
    // (McpToolFactory.BuildWorkspaceCapabilitiesTool/BuildWorkspaceSearchTool wire them the same way).
    public static readonly (string McpName, string ToolClass)[] HandoverExecuteTools =
    [
        ("workspace_capabilities", "WorkspaceCapabilitiesTool"),
        ("workspace_search", "WorkspaceSearchTool"),
    ];

    public static IEnumerable<object[]> HandoverExecuteToolsData() =>
        HandoverExecuteTools.Select(t => new object[] { t.McpName, t.ToolClass });

    [Theory]
    [MemberData(nameof(HandoverExecuteToolsData))]
    public void McpMethodCallsItsToolClassExecute(string mcpName, string toolClass)
    {
        string source = File.ReadAllText(Path.Combine(RepoPaths.Root, "mcp.cs"));
        string body = MethodBody(source, mcpName);
        Assert.True(
            Regex.IsMatch(body, $@"new\s+{Regex.Escape(toolClass)}\s*\(.*\)\s*\.\s*Execute\s*\(", RegexOptions.Singleline, RegexTimeout.Span),
            $"mcp.cs's {mcpName}() must call new {toolClass}(...).Execute(...) instead of holding its own inline logic.\n---\n{body}");
    }

    // All 24 golden MCP tools together: every one is in one of the three wired lists above.
    [Fact]
    public void AllTwentyFourToolsAreAccountedFor()
    {
        HashSet<string> wired =
        [
            .. StoreFactsMemoryDocsTools.Select(t => t.McpName)
,
            .. GraphAndBrainTools.Select(t => t.McpName),
            .. HandoverExecuteTools.Select(t => t.McpName),
        ];
        Assert.Equal(24, wired.Count);
    }

    // Extracts the body of `public static string <name>(...) { ... }` in mcp.cs, from its opening
    // brace to the matching closing brace (methods here never nest braces inside a string literal).
    private static string MethodBody(string source, string methodName)
    {
        Match signature = Regex.Match(source, $@"public static string {Regex.Escape(methodName)}\s*\([^)]*\)\s*\{{", RegexOptions.None, RegexTimeout.Span);
        Assert.True(signature.Success, $"mcp.cs has no method named {methodName}");
        int start = signature.Index + signature.Length;
        int depth = 1;
        int i = start;
        while (i < source.Length && depth > 0)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}') depth--;
            i++;
        }
        return source[start..(i - 1)];
    }
}
