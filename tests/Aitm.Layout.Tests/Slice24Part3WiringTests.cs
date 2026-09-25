using System.Text.RegularExpressions;
using Xunit;

namespace Aitm.Layout.Tests;

// RESTRUCTURE.md slice 24 ("aitm.cs and mcp.cs now hold only dispatch to the registry"), CLI side, part
// 3 of the lane: every golden CLI verb whose tool lives in Aitm.Graph/Tools gets its aitm.cs case
// rewired to call that tool class, in place of the inline logic (or the inline helper function) the
// case used to carry. This is red before the wiring lands (no case yet calls a tool class) and green
// after.
public class Slice24Part3WiringTests
{
    // verb -> the tool type its aitm.cs case has to call.
    public static readonly (string Verb, string ToolType)[] WiredVerbs =
    [
        ("project", "ProjectTool"),
        ("projects", "ProjectsTool"),
        ("forget-project", "ForgetProjectTool"),
        ("extract-edges", "ExtractEdgesTool"),
        ("candidates", "CandidatesTool"),
        ("promote", "PromoteTool"),
        ("promote-all", "PromoteAllTool"),
        ("seed-edges", "SeedEdgesTool"),
        ("impact", "ImpactTool"),
        ("graph-query", "GraphQueryTool"),
        ("graph-path", "GraphPathTool"),
        ("graph-explain", "GraphExplainTool"),
    ];

    [Fact]
    public void PartThreeCovers12OfThe71GoldenCliVerbs() => Assert.Equal(12, WiredVerbs.Length);

    [Fact]
    public void AitmCsReferencesTheAitmGraphProject()
    {
        string source = File.ReadAllText(Path.Combine(RepoPaths.Root, "aitm.cs"));
        Assert.Contains("#:project src/Aitm.Graph/Aitm.Graph.csproj", source);
    }

    [Theory]
    [MemberData(nameof(WiredVerbCases))]
    public void EveryWiredVerbsCaseCallsItsToolClass(string verb, string toolType)
    {
        string source = File.ReadAllText(Path.Combine(RepoPaths.Root, "src", "Aitm.Server", "Data", "CliDispatch.cs"));
        Match caseMatch = Regex.Match(source, $"case \"{Regex.Escape(verb)}\":");
        Assert.True(caseMatch.Success, $"aitm.cs has no case for '{verb}'");

        // Bound the block at the next top-level "case " or "default:" rather than the first "break;" —
        // a case whose body refuses early (seed-edges' missing-file guard, graph-path's usage guard)
        // has its own inner "break;" before the line that actually calls the tool class.
        Match nextCase = Regex.Match(source[(caseMatch.Index + 1)..], "\\n    (case \"|default:)");
        int blockEnd = nextCase.Success ? caseMatch.Index + 1 + nextCase.Index : source.Length;
        string block = source[caseMatch.Index..blockEnd];
        Assert.True(block.Contains($"new {toolType}", StringComparison.Ordinal),
            $"case \"{verb}\" does not call new {toolType}(...)");
    }

    public static IEnumerable<object[]> WiredVerbCases() =>
        WiredVerbs.Select(v => new object[] { v.Verb, v.ToolType });
}
