using System.Text.RegularExpressions;
using Xunit;

namespace Aitm.Layout.Tests;

// RESTRUCTURE.md slice 24: "aitm.cs and mcp.cs now hold only dispatch to the registry." This part
// wires the Aitm.Store/Facts/Memory/Docs tools first (the MCP side). Each listed [McpServerTool]
// method in mcp.cs must call its tool class's ExecuteMcp instead of carrying its own inline copy of
// the logic — a mechanical grep on the source, not a behavioural test (the per-tool oracle tests in
// Aitm.Store.Tests/Aitm.Facts.Tests/Aitm.Memory.Tests/Aitm.Docs.Tests cover behaviour).
public class McpDispatchTests
{
    // MCP tool name -> the tool class whose ExecuteMcp it must call (RESTRUCTURE.md section 2.2,
    // "Aitm.Store (1)", "Aitm.Facts (3)", "Aitm.Memory (3)", "Aitm.Docs (1)").
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
            Regex.IsMatch(body, $@"new\s+{Regex.Escape(toolClass)}\s*\(.*\)\s*\.\s*ExecuteMcp\s*\(", RegexOptions.Singleline),
            $"mcp.cs's {mcpName}() must call new {toolClass}(...).ExecuteMcp(...) instead of holding its own inline logic.\n---\n{body}");
    }

    // RESTRUCTURE.md slice 24, MCP lane part 2: the remaining 17 tools — Aitm.Graph (4), Aitm.Brain
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
            Regex.IsMatch(body, $@"new\s+{Regex.Escape(toolClass)}\s*\(.*\)\s*\.\s*ExecuteMcp\s*\(", RegexOptions.Singleline),
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
            Regex.IsMatch(body, $@"new\s+{Regex.Escape(toolClass)}\s*\(.*\)\s*\.\s*Execute\s*\(", RegexOptions.Singleline),
            $"mcp.cs's {mcpName}() must call new {toolClass}(...).Execute(...) instead of holding its own inline logic.\n---\n{body}");
    }

    // idp_token is the ONE exception (RESTRUCTURE.md slice 24 MCP part 2 card): the new
    // IdPTokenTool writes the token to a file and returns path + claims (slice 23c), while
    // mcp.cs's inline idp_token still returns the token itself — a planned output change that
    // does not land until RESTRUCTURE.md slice 28. Wiring it here would silently change behaviour a
    // client can see, so it stays inline until that slice, and this test documents the exemption
    // instead of asserting dispatch for it.
    [Fact]
    public void IdPTokenStaysInlineUntilSlice28()
    {
        string source = File.ReadAllText(Path.Combine(RepoPaths.Root, "mcp.cs"));
        string body = MethodBody(source, "idp_token");
        Assert.False(
            Regex.IsMatch(body, @"new\s+IdPTokenTool\s*\(.*\)\s*\.\s*Execute\s*\(", RegexOptions.Singleline),
            "idp_token is the documented exception (RESTRUCTURE.md slice 28): it must stay inline, " +
            "returning the token itself, until slice 28 accepts the file-path-and-claims output change.");
    }

    // All 25 golden MCP tools together: every one is either in one of the three wired lists above, or
    // is the one documented exception.
    [Fact]
    public void AllTwentyFiveToolsAreAccountedFor()
    {
        HashSet<string> wired = StoreFactsMemoryDocsTools.Select(t => t.McpName)
            .Concat(GraphAndBrainTools.Select(t => t.McpName))
            .Concat(HandoverExecuteTools.Select(t => t.McpName))
            .ToHashSet();
        wired.Add("idp_token");
        Assert.Equal(25, wired.Count);
    }

    // Extracts the body of `public static string <name>(...) { ... }` in mcp.cs, from its opening
    // brace to the matching closing brace (methods here never nest braces inside a string literal).
    private static string MethodBody(string source, string methodName)
    {
        Match signature = Regex.Match(source, $@"public static string {Regex.Escape(methodName)}\s*\([^)]*\)\s*\{{");
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
