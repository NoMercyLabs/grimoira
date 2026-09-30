using System.Diagnostics;
using System.Text.RegularExpressions;
using Grimora.Store.Data;
using Xunit;

namespace Grimora.Layout.Tests;

// Rule (2026-09-27): "i see powershell scripts but no bash equivalents". Every root *.ps1 has a *.sh twin with
// the same flags, and the other way round; CI on Linux runs the twins, never pwsh.
public partial class EveryPowerShellScriptHasABashTwinTests
{
    [GeneratedRegex(@"^param\((.*?)\)\s*$", RegexOptions.Singleline | RegexOptions.Multiline, RegexTimeout.Milliseconds)]
    private static partial Regex ParamBlock();

    [GeneratedRegex(@"\$(\w+)", RegexOptions.None, RegexTimeout.Milliseconds)]
    private static partial Regex ParamName();

    [GeneratedRegex("(?<!^)([A-Z])", RegexOptions.None, RegexTimeout.Milliseconds)]
    private static partial Regex Capital();

    private static string[] Tracked(string pattern)
    {
        ProcessStartInfo psi = new("git") { RedirectStandardOutput = true, WorkingDirectory = RepoPaths.Root };
        psi.ArgumentList.Add("ls-files");
        psi.ArgumentList.Add("-s");
        psi.ArgumentList.Add(pattern);
        using Process p = Process.Start(psi)!;
        string output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }

    private static string NameOf(string lsFilesLine) => lsFilesLine[(lsFilesLine.IndexOf('\t') + 1)..].Trim();

    private static string[] RootScripts(string extension) =>
        [.. Tracked("*" + extension).Select(NameOf).Where(n => !n.Contains('/')).Order()];

    public static TheoryData<string> PowerShellScripts()
    {
        TheoryData<string> data = [];
        foreach (string s in RootScripts(".ps1")) data.Add(s);
        return data;
    }

    [Theory]
    [MemberData(nameof(PowerShellScripts))]
    public void EveryPowerShellScriptHasABashTwin(string ps1)
    {
        string sh = Path.ChangeExtension(ps1, ".sh");
        string[] entry = [.. Tracked(sh).Where(l => NameOf(l) == sh)];
        Assert.True(entry.Length == 1, $"{ps1} has no tracked bash twin {sh}.");
        Assert.StartsWith("100755", entry[0]);
        string first = File.ReadLines(Path.Combine(RepoPaths.Root, sh)).First();
        Assert.Equal("#!/usr/bin/env bash", first);
    }

    [Fact]
    public void EveryBashScriptHasAPowerShellTwin()
    {
        foreach (string sh in RootScripts(".sh"))
            Assert.True(File.Exists(Path.Combine(RepoPaths.Root, Path.ChangeExtension(sh, ".ps1"))), $"{sh} has no PowerShell twin.");
    }

    [Theory]
    [MemberData(nameof(PowerShellScripts))]
    public void TwinsNameTheSameFlags(string ps1)
    {
        string sh = Path.ChangeExtension(ps1, ".sh");
        string ps = File.ReadAllText(Path.Combine(RepoPaths.Root, ps1));
        Match block = ParamBlock().Match(ps);
        if (!block.Success) return;
        string bash = File.ReadAllText(Path.Combine(RepoPaths.Root, sh));
        foreach (Match m in ParamName().Matches(block.Groups[1].Value))
        {
            string flag = "--" + Capital().Replace(m.Groups[1].Value, "-$1").ToLowerInvariant();
            Assert.True(bash.Contains(flag, StringComparison.Ordinal), $"{sh} does not name the flag {flag} that {ps1} takes as -{m.Groups[1].Value}.");
        }
    }

    [Fact]
    public void CiRunsTheBashTwinsNotPwsh()
    {
        string ci = File.ReadAllText(Path.Combine(RepoPaths.Root, ".github", "workflows", "ci.yml"));
        Assert.DoesNotContain("pwsh", ci, StringComparison.Ordinal);
        Assert.DoesNotContain(".ps1", ci, StringComparison.Ordinal);
    }
}
