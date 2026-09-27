using System.Diagnostics;
using Xunit;

namespace Grimora.Layout.Tests;

// RESTRUCTURE.md slice 29f/33: bin-cli-old/ (the kept grimora.cs build) was the rollback and the parity
// tests' oracle while grimora.cs's verbs still moved to their tool classes one slice at a time. Every
// oracle-comparison test class now replays a frozen golden (CliGoldens) instead of running that binary, so
// nothing builds or reads bin-cli-old any more. This guards the retirement: no tracked file or folder is
// named bin-cli-old, and no build script or workflow still mentions it.
public sealed class NoSecondImplementationRemainsTests
{
    [Fact]
    public void NoTrackedFileOrFolderIsNamedBinCliOld()
    {
        string[] tracked = RunGit("ls-files").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        string[] hits = [.. tracked.Where(f => f.Contains("bin-cli-old", StringComparison.Ordinal))];
        Assert.True(hits.Length == 0, $"tracked path(s) still name bin-cli-old: {string.Join(", ", hits)}");
    }

    [Theory]
    [InlineData("build-cli.ps1")]
    [InlineData("build-cli.sh")]
    [InlineData("build.ps1")]
    [InlineData("build.sh")]
    [InlineData("verify.ps1")]
    [InlineData("verify.sh")]
    [InlineData(".gitignore")]
    [InlineData(".github/workflows/ci.yml")]
    [InlineData(".github/workflows/release.yml")]
    public void ScriptOrWorkflowNeverMentionsBinCliOld(string relative)
    {
        string path = Path.Combine(RepoPaths.Root, relative);
        if (!File.Exists(path)) return; // not every repo state has every workflow file; nothing to check.
        string text = File.ReadAllText(path);
        Assert.DoesNotContain("bin-cli-old", text, StringComparison.Ordinal);
    }

    private static string RunGit(string arguments)
    {
        ProcessStartInfo psi = new("git", $"-C \"{RepoPaths.Root}\" {arguments}")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
        };
        using Process process = Process.Start(psi) ?? throw new InvalidOperationException("could not start git");
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"git {arguments} failed: {stderr}");
        return stdout;
    }
}
