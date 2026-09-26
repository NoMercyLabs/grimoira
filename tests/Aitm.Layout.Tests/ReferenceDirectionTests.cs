using Aitm.Store.Data;
using System.Text.RegularExpressions;
using Xunit;

namespace Aitm.Layout.Tests;

// RESTRUCTURE.md section 1: "Why this order has no cycles ... A layout test fails when a project
// reference points down-to-up." Each project's level is the highest level any of its allowed
// dependencies may sit at; a ProjectReference to a project at the same or a higher level is a cycle
// or an upward reference and fails the test.
public partial class ReferenceDirectionTests
{
    private static readonly Dictionary<string, int> Level = new()
    {
        ["Aitm.Store"] = 0,
        ["Aitm.Facts"] = 1,
        ["Aitm.Memory"] = 1,
        ["Aitm.Docs"] = 1,
        ["Aitm.Graph"] = 1,
        ["Aitm.Brain"] = 2,
        ["Aitm.Hooks"] = 3,
        ["Aitm.Server"] = 4,
        ["Aitm.Cli"] = 4,
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
    public void CliReferencesNothingInAitm()
    {
        // Section 1: Cli "may depend on nothing in AITM except a small shared request contract,
        // which it copies from Store at build time (a linked source file). It never opens a store
        // itself." No project reference at all is expected in slice 2.
        Assert.Empty(ReadProjectReferences("Aitm.Cli"));
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
