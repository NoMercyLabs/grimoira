using System.Text.RegularExpressions;
using Xunit;

namespace Aitm.Layout.Tests;

// RESTRUCTURE.md slice 24 ("aitm.cs and mcp.cs now hold only dispatch to the registry"), CLI lane part
// 4a of the lane: the Brain-read sub-verbs nested inside aitm.cs's `brain` command (BrainCmd's own
// switch, aitm.cs:852) get their case rewired to call their tool class, in place of the inline local
// function the case used to carry — the same move part 1 and part 2 made for their verbs, one level
// deeper: `brain <sub>` is a nested `case "<sub>":` inside `BrainCmd`, not a top-level case in the outer
// switch, but the regex below finds it wherever it lives.
//
// The top-level `case "brain":` itself (aitm.cs:112) is untouched: it still calls `BrainCmd(...)`,
// because BrainCmd also dispatches sub-verbs this part does not own (recall, impact, learn, learn-batch,
// set-hard, stats, merge, forget, unlink, tidy, distill, seed — GoldenListsTests' 32-verb Brain group),
// most of which are not wired to a tool class yet. Rewiring the outer case to call a router directly
// (e.g. `new BrainTool()`) would silently drop those unwired sub-verbs' behaviour, so this part leaves
// BrainCmd in place and only rewires the bodies of the 10 sub-verb cases it owns. This is red before the
// wiring lands (no case yet calls a tool class) and green after.
public class BrainReadVerbDispatchTests
{
    // sub-verb -> the tool type its nested case (inside BrainCmd) has to call.
    public static readonly (string Verb, string ToolType)[] WiredVerbs =
    [
        ("core", "BrainCoreTool"),
        ("scope", "BrainScopeTool"),
        ("common", "BrainCommonTool"),
        ("place", "BrainPlaceTool"),
        ("why", "BrainWhyTool"),
        ("stale", "BrainStaleTool"),
        ("gaps", "BrainGapsTool"),
        ("verify", "BrainVerifyTool"),
        ("audit", "BrainAuditTool"),
        ("export", "BrainExportTool"),
    ];

    [Fact]
    public void PartFourACovers10OfThe71GoldenCliVerbs() => Assert.Equal(10, WiredVerbs.Length);

    [Theory]
    [MemberData(nameof(WiredVerbCases))]
    public void EveryWiredVerbsCaseCallsItsToolClass(string verb, string toolType)
    {
        string source = File.ReadAllText(Path.Combine(RepoPaths.Root, "src", "Aitm.Server", "Data", "CliDispatch.cs"));
        Match caseMatch = Regex.Match(source, $"case \"{Regex.Escape(verb)}\":");
        Assert.True(caseMatch.Success, $"aitm.cs has no case for '{verb}'");

        int blockEnd = source.IndexOf("break;", caseMatch.Index, StringComparison.Ordinal);
        Assert.True(blockEnd > 0, $"case \"{verb}\" has no break; to bound the block");
        string block = source[caseMatch.Index..blockEnd];
        Assert.True(block.Contains($"new {toolType}", StringComparison.Ordinal),
            $"case \"{verb}\" does not call new {toolType}(...)");
    }

    public static IEnumerable<object[]> WiredVerbCases() =>
        WiredVerbs.Select(v => new object[] { v.Verb, v.ToolType });

    // The outer dispatcher case still exists and still routes through BrainCmd (see the class comment).
    [Fact]
    public void TopLevelBrainCaseStillDispatchesThroughBrainCmd()
    {
        string source = File.ReadAllText(Path.Combine(RepoPaths.Root, "src", "Aitm.Server", "Data", "CliDispatch.cs"));
        Match caseMatch = Regex.Match(source, "case \"brain\":");
        Assert.True(caseMatch.Success, "aitm.cs has no case for 'brain'");
        int blockEnd = source.IndexOf("break;", caseMatch.Index, StringComparison.Ordinal);
        string block = source[caseMatch.Index..blockEnd];
        Assert.Contains("BrainCmd(", block, StringComparison.Ordinal);
    }
}
