using System.Text.RegularExpressions;
using Xunit;

namespace Aitm.Layout.Tests;

// RESTRUCTURE.md slice 24 ("aitm.cs and mcp.cs now hold only dispatch to the registry"), CLI side, part
// 4b of the lane: the Aitm.Brain write verbs — the ones that add, correct, dedup, retire or bulk-rewrite
// graph rows — get their aitm.cs case rewired to call their tool class, in place of the inline logic the
// case used to carry. `shed-node` lives as a top-level `case "shed-node":` (not under `brain`); the other
// eight are `brain <sub>` cases inside the nested BrainCmd switch.
// This is red before the wiring lands (no case yet calls a tool class) and green after.
public partial class BrainWriteVerbDispatchTests
{
    // verb -> the tool type its aitm.cs case has to call.
    public static readonly (string Verb, string ToolType)[] WiredVerbs =
    [
        ("learn", "BrainLearnTool"),
        ("learn-batch", "BrainLearnBatchTool"),
        ("set-hard", "BrainSetHardTool"),
        ("merge", "BrainMergeTool"),
        ("forget", "BrainForgetTool"),
        ("unlink", "BrainUnlinkTool"),
        ("shed-node", "ShedNodeTool"),
        ("tidy", "BrainTidyTool"),
        ("distill", "BrainDistillTool"),
    ];

    [Fact]
    public void PartFourBCovers9OfThe71GoldenCliVerbs() => Assert.Equal(9, WiredVerbs.Length);

    [Theory]
    [MemberData(nameof(WiredVerbCases))]
    public void EveryWiredVerbsCaseCallsItsToolClass(string verb, string toolType)
    {
        string source = File.ReadAllText(Path.Combine(RepoPaths.Root, "src", "Aitm.Server", "Data", "CliDispatch.cs"));
        Match caseMatch = Regex.Match(source, $"case \"{Regex.Escape(verb)}\":");
        Assert.True(caseMatch.Success, $"aitm.cs has no case for '{verb}'");

        // Several of these cases keep an inline arg-count usage check ("if (...) { Console.WriteLine(...);
        // break; }") ahead of the tool call, unlike part 1/2's cases — a plain "first break;" search would
        // stop at that inner break and miss the tool call after it. Bound the block by the next case/default
        // label instead, which still cannot cross into a different verb's block.
        int searchFrom = caseMatch.Index + caseMatch.Length;
        Match next = NextCaseOrDefault().Match(source[searchFrom..]);
        int blockEnd = next.Success ? searchFrom + next.Index : source.Length;
        string block = source[caseMatch.Index..blockEnd];
        Assert.True(block.Contains($"new {toolType}", StringComparison.Ordinal),
            $"case \"{verb}\" does not call new {toolType}(...)");
    }

    public static IEnumerable<object[]> WiredVerbCases() =>
        WiredVerbs.Select(v => new object[] { v.Verb, v.ToolType });

    [GeneratedRegex("case \"[^\"]+\":|default:")]
    private static partial Regex NextCaseOrDefault();
}
