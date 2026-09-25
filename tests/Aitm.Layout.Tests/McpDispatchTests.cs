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
