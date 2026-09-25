using System.Text.RegularExpressions;
using Xunit;

namespace Aitm.Layout.Tests;

// RESTRUCTURE.md slice 24 ("aitm.cs and mcp.cs now hold only dispatch to the registry"), CLI side, part
// 1 of the lane: every golden CLI verb whose tool lives in Aitm.Store/Tools or Aitm.Facts/Tools gets its
// aitm.cs case rewired to call that tool class, in place of the inline logic the case used to carry.
// This is red before the wiring lands (no case yet calls a tool class) and green after.
public class Slice24Part1WiringTests
{
    // verb -> the tool type its aitm.cs case has to call.
    public static readonly (string Verb, string ToolType)[] WiredVerbs =
    [
        ("init", "InitTool"),
        ("import", "ImportTool"),
        ("backup", "BackupTool"),
        ("stats", "StatsTool"),
        ("history", "HistoryTool"),
        ("query", "QueryTool"),
        ("add", "AddTool"),
        ("shed-fact", "ShedFactTool"),
        ("todo", "TodoTool"),
        ("todos", "TodosTool"),
        ("done", "DoneTool"),
        ("finding", "FindingTool"),
        ("findings", "FindingsTool"),
        ("resolve", "ResolveTool"),
        ("index-packages", "IndexPackagesTool"),
    ];

    [Fact]
    public void PartOneCovers15OfThe71GoldenCliVerbs() => Assert.Equal(15, WiredVerbs.Length);

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
}
