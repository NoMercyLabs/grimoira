using Grimora.Store.Data;
using System.Text.RegularExpressions;
using Xunit;

namespace Grimora.Layout.Tests;

// RESTRUCTURE.md sub-card 29e: bin-cli/ is the published Grimora.Cli (the thin client of POST /cli), and the
// last grimora.cs build is kept beside it as bin-cli-old/ so a rollback is a file swap. bin-cli-next/ (slice 29
// part 1) retires. The build scripts and the ignore list must agree on that layout, and every test oracle
// that means "the grimora.cs build" must read bin-cli-old/ (bin-cli/ now forwards to a running server). The
// old-vs-new behaviour is pinned in Grimora.Server.Tests.BinCliThinClientMatchesBinCliOldTests.
public partial class BinCliIsThePublishedThinClientTests
{
    [Fact]
    public void BuildCliScriptPublishesGrimoraCliToBinCliAndKeepsGrimoraCsAsBinCliOld()
    {
        string script = File.ReadAllText(Path.Combine(RepoPaths.Root, "build-cli.ps1"));
        Assert.Matches(PublishCliCommand(), script);
        Assert.Matches(BuildOldCliCommand(), script);
        Assert.DoesNotContain("bin-cli-next", script);
    }

    [Fact]
    public void BuildScriptNeverWritesTheGrimoraCsBuildOverBinCli()
    {
        string script = File.ReadAllText(Path.Combine(RepoPaths.Root, "build.ps1"));
        Assert.DoesNotMatch(OldCliBuiltIntoBinCli(), script);
        Assert.Contains("build-cli.ps1", script);
    }

    [Fact]
    public void GitIgnoresBinCliOldAndNoLongerNamesBinCliNext()
    {
        string[] lines = File.ReadAllLines(Path.Combine(RepoPaths.Root, ".gitignore"));
        Assert.Contains("bin-cli/", lines);
        Assert.Contains("bin-cli-old/", lines);
        Assert.DoesNotContain("bin-cli-next/", lines);
    }

    [Theory]
    [InlineData("tests/Grimora.TestSupport/GrimoraCliRunner.cs")]
    [InlineData("tests/Grimora.TestSupport/OldVsNewCli.cs")]
    public void GrimoraCsOraclesReadBinCliOld(string relative)
    {
        string source = File.ReadAllText(Path.Combine(RepoPaths.Root, relative));
        Assert.Contains("\"bin-cli-old\"", source);
        Assert.DoesNotContain("\"bin-cli\"", source);
    }

    [Fact]
    public void BuildCliScriptClearsBinCliBeforePublishingSoNoStaleDllsRemain()
    {
        string script = File.ReadAllText(Path.Combine(RepoPaths.Root, "build-cli.ps1"));
        int removeIndex = script.IndexOf("Remove-Item", StringComparison.Ordinal);
        int publishIndex = script.IndexOf(@"-o ""$PSScriptRoot/bin-cli""", StringComparison.Ordinal);
        Assert.True(removeIndex >= 0,
            "build-cli.ps1 never clears bin-cli/ before publishing into it, so an old grimora.cs build's DLLs remain beside the thin client.");
        Assert.True(publishIndex >= 0, "the bin-cli publish line was not found; the assertion above needs updating to match its new shape.");
        Assert.True(removeIndex < publishIndex, "bin-cli/ must be cleared before the publish that fills it back in, not after.");
    }

    [GeneratedRegex(@"dotnet publish ""\$PSScriptRoot/src/Grimora\.Cli/Grimora\.Cli\.csproj"" -c Release -o ""\$PSScriptRoot/bin-cli""", RegexOptions.None, RegexTimeout.Milliseconds)]
    private static partial Regex PublishCliCommand();
    [GeneratedRegex(@"dotnet build ""\$PSScriptRoot/grimora\.cs"" -c Release -o ""\$PSScriptRoot/bin-cli-old""", RegexOptions.None, RegexTimeout.Milliseconds)]
    private static partial Regex BuildOldCliCommand();
    [GeneratedRegex(@"grimora\.cs""[^\r\n]*-o ""\$root/bin-cli""", RegexOptions.None, RegexTimeout.Milliseconds)]
    private static partial Regex OldCliBuiltIntoBinCli();
}
