using Grimoira.Brain.Tools;
using Grimoira.Docs.Tools;
using Grimoira.Facts.Tools;
using Grimoira.Graph.Tools;
using Grimoira.Hooks.Tools;
using Grimoira.Memory.Tools;
using Grimoira.Server.Handover;
using Grimoira.Store.Data;
using Grimoira.Store.Tools;

namespace Grimoira.Server.Data;

/// <summary>
/// The 25 golden MCP tools (tests/Grimoira.Layout.Tests/GoldenListsTests.cs), gathered into one
/// <see cref="ToolRegistry"/> so <see cref="McpToolFactory"/> maps them through a single path rather
/// than 25 hand-written endpoints (RESTRUCTURE.md "Slice 26"). Every entry is an already-moved
/// <see cref="ITool"/> class; nothing here re-implements a tool's behaviour.
/// </summary>
public static class AllMcpTools
{
    public static ToolRegistry BuildRegistry()
    {
        IUsageSignal usageSignal = new UsageSignal();
        return new(
        [
        new HistoryTool(),
        new QueryTool(usageSignal),
        new FindingTool(),
        new FindingsTool(),
        new MemTool(usageSignal),
        new ShedMemoryTool(),
        new RecallTool(),
        new ChatQueryTool(),
        new ChatCountTool(),
        new DocTool(),
        new ImpactTool(),
        new GraphQueryTool(),
        new GraphPathTool(),
        new GraphExplainTool(),
        new BrainCoreTool(),
        new BrainScopeTool(),
        new BrainCommonTool(),
        new BrainPlaceTool(),
        new BrainRecallTool(),
        new BrainImpactTool(),
        new BrainLearnTool(),
        new BrainGapsTool(),
        new BrainStageTool(),
        new BrainFlushTool(),
        new WorkspaceCapabilitiesTool(),
        new WorkspaceSearchTool(),
        new PatternsTool(),
        ]);
    }
}
