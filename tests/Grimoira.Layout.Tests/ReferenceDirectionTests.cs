using Grimoira.Store.Data;
using System.Text.RegularExpressions;
using Xunit;

namespace Grimoira.Layout.Tests;

// RESTRUCTURE.md section 1: "Why this order has no cycles ... A layout test fails when a project
// reference points down-to-up." Each project's level is the highest level any of its allowed
// dependencies may sit at; a ProjectReference to a project at the same or a higher level is a cycle
// or an upward reference and fails the test.
public partial class ReferenceDirectionTests
{
    private static readonly Dictionary<string, int> Level = new()
    {
        ["Grimoira.Store"] = 0,
        ["Grimoira.Facts"] = 1,
        ["Grimoira.Memory"] = 1,
        ["Grimoira.Docs"] = 1,
        ["Grimoira.Graph"] = 1,
        ["Grimoira.Brain"] = 2,
        ["Grimoira.Hooks"] = 3,
        ["Grimoira.Server"] = 4,
        ["Grimoira.Cli"] = 4,
    };

    [Theory]
    [MemberData(nameof(RepoPaths.SourceProjectsData), MemberType = typeof(RepoPaths))]
    public void EveryReferencePointsToALowerLevel(string project)
    {
        foreach (string referenced in ReadProjectReferences(project))
        {
            Assert.True(Level.TryGetValue(referenced, out int refLevel),
                $"{project} references unknown project {referenced}");
            Assert.True(refLevel < Level[project],
                $"{project} (level {Level[project]}) references {referenced} (level {refLevel}): reference must point down, not sideways or up");
        }
    }

    [Fact]
    public void CliReferencesNothingInGrimoira()
    {
        // Section 1: Cli "may depend on nothing in Grimoira except a small shared request contract,
        // which it copies from Store at build time (a linked source file). It never opens a store
        // itself." No project reference at all is expected in slice 2.
        Assert.Empty(ReadProjectReferences("Grimoira.Cli"));
    }

    private static IEnumerable<string> ReadProjectReferences(string project)
    {
        string csproj = Path.Combine(RepoPaths.Root, "src", project, $"{project}.csproj");
        string text = File.ReadAllText(csproj);
        foreach (Match m in ProjectReferenceInclude().Matches(text))
        {
            yield return Path.GetFileNameWithoutExtension(m.Groups[1].Value);
        }
    }

    [GeneratedRegex("ProjectReference Include=\"([^\"]+)\"", RegexOptions.None, RegexTimeout.Milliseconds)]
    private static partial Regex ProjectReferenceInclude();
}
