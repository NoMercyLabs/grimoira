using System.Text.RegularExpressions;
using Xunit;

namespace Aitm.Layout.Tests;

// RESTRUCTURE.md sub-card 29e: bin-cli/ is the published Aitm.Cli (the thin client of POST /cli), and the
// last aitm.cs build is kept beside it as bin-cli-old/ so a rollback is a file swap. bin-cli-next/ (slice 29
// part 1) retires. The build scripts and the ignore list must agree on that layout, and every test oracle
// that means "the aitm.cs build" must read bin-cli-old/ (bin-cli/ now forwards to a running server). The
// old-vs-new behaviour is pinned in Aitm.Server.Tests.BinCliThinClientMatchesBinCliOldTests.
public class BinCliIsThePublishedThinClientTests
{
    [Fact]
    public void BuildCliScriptPublishesAitmCliToBinCliAndKeepsAitmCsAsBinCliOld()
    {
        string script = File.ReadAllText(Path.Combine(RepoPaths.Root, "build-cli.ps1"));
        Assert.Matches(new Regex(@"dotnet publish ""\$PSScriptRoot/src/Aitm\.Cli/Aitm\.Cli\.csproj"" -c Release -o ""\$PSScriptRoot/bin-cli"""), script);
        Assert.Matches(new Regex(@"dotnet build ""\$PSScriptRoot/aitm\.cs"" -c Release -o ""\$PSScriptRoot/bin-cli-old"""), script);
        Assert.DoesNotContain("bin-cli-next", script);
    }

    [Fact]
    public void BuildScriptNeverWritesTheAitmCsBuildOverBinCli()
    {
        string script = File.ReadAllText(Path.Combine(RepoPaths.Root, "build.ps1"));
        Assert.DoesNotMatch(new Regex(@"aitm\.cs""[^\r\n]*-o ""\$root/bin-cli"""), script);
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
    [InlineData("tests/Aitm.TestSupport/AitmCliRunner.cs")]
    [InlineData("tests/Aitm.TestSupport/OldVsNewCli.cs")]
    [InlineData("tests/Aitm.Store.Tests/Support/V3StoreFixture.cs")]
    public void AitmCsOraclesReadBinCliOld(string relative)
    {
        string source = File.ReadAllText(Path.Combine(RepoPaths.Root, relative));
        Assert.Contains("\"bin-cli-old\"", source);
        Assert.DoesNotContain("\"bin-cli\"", source);
    }

    [Theory]
    [InlineData("cli-exit.test.mjs")]
    [InlineData("ranking-agreement.test.mjs")]
    [InlineData("mcp-graph.test.mjs")]
    public void NodeTestsOfTheAitmCsBuildReadBinCliOld(string relative)
    {
        string source = File.ReadAllText(Path.Combine(RepoPaths.Root, relative));
        Assert.Contains("'bin-cli-old'", source);
        Assert.DoesNotContain("'bin-cli'", source);
    }

    [Fact]
    public void BuildCliScriptClearsBinCliBeforePublishingSoNoStaleDllsRemain()
    {
        string script = File.ReadAllText(Path.Combine(RepoPaths.Root, "build-cli.ps1"));
        int removeIndex = script.IndexOf("Remove-Item", StringComparison.Ordinal);
        int publishIndex = script.IndexOf(@"-o ""$PSScriptRoot/bin-cli""", StringComparison.Ordinal);
        Assert.True(removeIndex >= 0,
            "build-cli.ps1 never clears bin-cli/ before publishing into it, so an old aitm.cs build's DLLs remain beside the thin client.");
        Assert.True(publishIndex >= 0, "the bin-cli publish line was not found; the assertion above needs updating to match its new shape.");
        Assert.True(removeIndex < publishIndex, "bin-cli/ must be cleared before the publish that fills it back in, not after.");
    }

    [Theory]
    [InlineData("tests/Aitm.Layout.Tests/BrainReadCliParityTests.cs")]
    [InlineData("tests/Aitm.Layout.Tests/GraphCliParityTests.cs")]
    public void ParityTestCommentsNameTheAitmCsBuildAsBinCliOldNotBinCli(string relative)
    {
        string source = File.ReadAllText(Path.Combine(RepoPaths.Root, relative));
        Assert.DoesNotContain("today's bin-cli/aitm.dll", source);
        Assert.Contains("today's bin-cli-old/aitm.dll", source);
    }
}
