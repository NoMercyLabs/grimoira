using System.Text.RegularExpressions;
using Xunit;

namespace Aitm.Layout.Tests;

// RESTRUCTURE.md slice 24 ("aitm.cs and mcp.cs now hold only dispatch to the registry"), CLI side, part
// 4c of the lane — the last part: the remaining Aitm.Brain verbs (the write-side brake and the curated
// spine) get their aitm.cs case rewired to call their tool class, in place of the inline function the
// case used to carry. `stage` and `flush` are top-level cases; `seed` (`brain seed`), `eval`,
// `spine-export` and `spine-import` are also top-level cases (`eval`/`spine-export`/`spine-import` sit
// beside `stage`/`flush` in the outer switch; `seed` is a `brain <sub>` case inside the nested BrainCmd
// switch). This is red before the wiring lands (no case yet calls a tool class) and green after.
public partial class StagingSpineEvalVerbDispatchTests
{
    // verb -> the tool type its aitm.cs case has to call.
    public static readonly (string Verb, string ToolType)[] WiredVerbs =
    [
        ("stage", "BrainStageTool"),
        ("flush", "BrainFlushTool"),
        ("seed", "BrainSeedTool"),
        ("spine-export", "SpineExportTool"),
        ("spine-import", "SpineImportTool"),
        ("eval", "EvalTool"),
    ];

    [Fact]
    public void PartFourCCoversTheLast6OfThe71GoldenCliVerbs() => Assert.Equal(6, WiredVerbs.Length);

    [Theory]
    [MemberData(nameof(WiredVerbCases))]
    public void EveryWiredVerbsCaseCallsItsToolClass(string verb, string toolType)
    {
        string source = File.ReadAllText(Path.Combine(RepoPaths.Root, "src", "Aitm.Server", "Data", "CliDispatch.cs"));
        Match caseMatch = Regex.Match(source, $"case \"{Regex.Escape(verb)}\":");
        Assert.True(caseMatch.Success, $"aitm.cs has no case for '{verb}'");

        // Bound the block by the next "case \"...\":"/"default:" label (regardless of nesting depth),
        // not the first "break;" — several of these cases (stage, spine-import, and "seed", which is
        // nested inside the BrainCmd switch) keep an inline usage/flag check ahead of the tool call, so
        // a plain "first break;" search would stop at that inner break and miss the call after it.
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
