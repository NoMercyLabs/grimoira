using Xunit;

namespace Grimora.Layout.Tests;

// Reviewer finding (chatgpt/codex/gemini/vscode-chat, 2026-09-27): agents/knowledge-lookup.md granted the
// whole "mcp__plugin_grimora_grimora__*" wildcard in its `tools:` frontmatter, relying only on prose
// ("never call brain_stage/brain_flush/...") to keep it from writing. A wildcard tool grant is not a
// safety boundary — anything that can get the agent to ignore its own prose (a prompt injection in a
// document it reads, a model that just makes a mistake) could still call a write tool. The agent must be
// read-only by declaration, not by promise: its tools: list should name exactly the read tools, never the
// wildcard, and never a write tool.
public sealed class KnowledgeLookupAgentIsReadOnlyTests
{
    private const string WriteToolPrefix = "mcp__plugin_grimora_grimora__";

    // The full write surface of the Grimora MCP server (McpName is non-null and the tool mutates the
    // store): staging/committing learned entries, learning a node directly, forgetting a memory, and
    // logging a finding. Kept as a literal list, not derived from source, so this test still catches a
    // future write tool that knowledge-lookup.md's author simply forgot to exclude.
    private static readonly string[] WriteTools =
    [
        "brain_stage", "brain_flush", "brain_learn", "shed_memory", "log_finding",
    ];

    private static string AgentMarkdown => File.ReadAllText(Path.Combine(RepoPaths.Root, "agents", "knowledge-lookup.md"));

    private static string[] DeclaredTools()
    {
        string? line = AgentMarkdown.Split('\n').Select(l => l.TrimEnd('\r'))
            .FirstOrDefault(l => l.StartsWith("tools:", StringComparison.Ordinal));
        Assert.True(line is not null, "knowledge-lookup.md must declare a tools: frontmatter list on its own line.");

        int open = line.IndexOf('[');
        int close = line.LastIndexOf(']');
        Assert.True(open >= 0 && close > open, "tools: must be a bracketed list.");

        return [.. line[(open + 1)..close]
            .Split(',')
            .Select(part => part.Trim().Trim('"'))
            .Where(part => part.Length > 0)];
    }

    [Fact]
    public void ToolsListNeverGrantsTheWholeGrimoraWildcard()
    {
        string[] tools = DeclaredTools();
        Assert.DoesNotContain(tools, t => t.Contains('*'));
    }

    [Theory]
    [MemberData(nameof(WriteToolNamesData))]
    public void ToolsListNeverGrantsAWriteTool(string writeTool)
    {
        string[] tools = DeclaredTools();
        Assert.DoesNotContain(WriteToolPrefix + writeTool, tools);
    }

    public static IEnumerable<object[]> WriteToolNamesData() => WriteTools.Select(t => new object[] { t });

    [Fact]
    public void ToolsListGrantsAtLeastTheCoreReadTools()
    {
        string[] tools = DeclaredTools();
        foreach (string readTool in new[] { "fact", "rule", "recall", "doc", "graph_query", "brain_recall" })
            Assert.Contains(WriteToolPrefix + readTool, tools);
    }
}
