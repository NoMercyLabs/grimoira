using Xunit;

namespace Grimora.Layout.Tests;

// the owner, 2026-09-27: every warning Rider shows must fail the build. Rider's own inspections are not
// Roslyn analyzers, so they run through JetBrains' inspectcode (a pinned dotnet tool) against the
// checked-in solution DotSettings, and inspect.ps1 fails on any result at severity ERROR.
public class RiderInspectionsAreEnforcedTests
{
    private static string Read(string relative) => File.ReadAllText(Path.Combine(RepoPaths.Root, relative));

    [Fact]
    public void Tool_manifest_pins_the_jetbrains_inspection_tool()
    {
        string text = Read(".config/dotnet-tools.json");
        Assert.Contains("jetbrains.resharper.globaltools", text);
        Assert.Contains("\"jb\"", text);
    }

    public static IEnumerable<object[]> NamedInspections() =>
        new[] { "CanSimplifySetAddingWithSingleCall", "ArrangeObjectCreationWhenTypeEvident", "MergeIntoPattern", "RedundantSuppressNullableWarningExpression", "UseCollectionExpression" }
            .Select(i => new object[] { i });

    [Theory]
    [MemberData(nameof(NamedInspections))]
    public void Solution_dotsettings_makes_the_inspection_an_error(string inspection)
    {
        string text = Read("Grimora.sln.DotSettings");
        Assert.Contains($"InspectionSeverities/={inspection}/@EntryIndexedValue\">ERROR<", text);
    }

    [Theory]
    [InlineData("verify.ps1")]
    [InlineData(".github/workflows/ci.yml")]
    public void Gate_is_wired_in(string file)
    {
        Assert.Contains("inspect.ps1", Read(file));
    }

    [Fact]
    public void Inspect_script_runs_inspectcode_and_fails_on_error_results()
    {
        string text = Read("inspect.ps1");
        Assert.Contains("jb inspectcode Grimora.sln", text);
        Assert.Contains("Sarif", text);
        Assert.Contains("'error'", text);
    }
}
