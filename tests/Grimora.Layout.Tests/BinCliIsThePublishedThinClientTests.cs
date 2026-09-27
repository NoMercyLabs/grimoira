using Grimora.Store.Data;
using System.Text.RegularExpressions;
using Xunit;

namespace Grimora.Layout.Tests;

// RESTRUCTURE.md sub-card 29e: bin-cli/ is the published Grimora.Cli (the thin client of POST /cli).
// bin-cli-old/ (the kept grimora.cs build, the rollback and the parity tests' oracle) retired in slice
// 29f/33 once every oracle-comparison test class was frozen to a golden (CliGoldens): a frozen class
// replays its golden instead of running a second binary, and the tests that still need a live
// pinned-commit build (InitFullTests, CliFlagCoverageGuardTests) build it themselves from git history.
// GrimoraCliRunner and OldVsNewCli's no-golden fallback both read bin-cli/ now, not a second binary.
public partial class BinCliIsThePublishedThinClientTests
{
    [Fact]
    public void BuildCliScriptPublishesGrimoraCliToBinCli()
    {
        string script = File.ReadAllText(Path.Combine(RepoPaths.Root, "build-cli.ps1"));
        Assert.Matches(PublishCliCommand(), script);
        Assert.DoesNotContain("bin-cli-old", script);
        Assert.DoesNotContain("bin-cli-next", script);
    }

    [Fact]
    public void BuildScriptNeverBuildsASecondCliBinary()
    {
        string script = File.ReadAllText(Path.Combine(RepoPaths.Root, "build.ps1"));
        Assert.DoesNotContain("bin-cli-old", script);
        Assert.Contains("build-cli.ps1", script);
    }

    [Fact]
    public void GitIgnoresBinCliAndNoLongerNamesBinCliOldOrBinCliNext()
    {
        string[] lines = File.ReadAllLines(Path.Combine(RepoPaths.Root, ".gitignore"));
        Assert.Contains("bin-cli/", lines);
        Assert.DoesNotContain("bin-cli-old/", lines);
        Assert.DoesNotContain("bin-cli-next/", lines);
    }

    [Theory]
    [InlineData("tests/Grimora.TestSupport/GrimoraCliRunner.cs")]
    [InlineData("tests/Grimora.TestSupport/OldVsNewCli.cs")]
    public void NoGoldenOracleFallbackReadsBinCliNotASecondBinary(string relative)
    {
        string source = File.ReadAllText(Path.Combine(RepoPaths.Root, relative));
        Assert.Contains("\"bin-cli\"", source);
        Assert.DoesNotContain("bin-cli-old", source);
    }

    [Fact]
    public void BuildCliScriptClearsBinCliBeforePublishingSoNoStaleDllsRemain()
    {
        string script = File.ReadAllText(Path.Combine(RepoPaths.Root, "build-cli.ps1"));
        int removeIndex = script.IndexOf("Remove-Item", StringComparison.Ordinal);
        int publishIndex = script.IndexOf(@"-o ""$PSScriptRoot/bin-cli""", StringComparison.Ordinal);
        Assert.True(removeIndex >= 0,
            "build-cli.ps1 never clears bin-cli/ before publishing into it, so a stale DLL could remain beside the thin client.");
        Assert.True(publishIndex >= 0, "the bin-cli publish line was not found; the assertion above needs updating to match its new shape.");
        Assert.True(removeIndex < publishIndex, "bin-cli/ must be cleared before the publish that fills it back in, not after.");
    }

    [GeneratedRegex(@"dotnet publish ""\$PSScriptRoot/src/Grimora\.Cli/Grimora\.Cli\.csproj"" -c Release -o ""\$PSScriptRoot/bin-cli""", RegexOptions.None, RegexTimeout.Milliseconds)]
    private static partial Regex PublishCliCommand();
}
