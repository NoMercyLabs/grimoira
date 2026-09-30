using Grimora.Store.Data;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Xunit;

namespace Grimora.Layout.Tests;

// Grimora is public. Nothing that ships (the product code, the plugin manifests, the hooks, skills,
// agents, commands, the git hooks, CI and the root scripts) names the owner or his agent: a transcript
// turn is "User" or "Assistant", never a person's name, and a rule is dated, never attributed.
// Word-bounded and case-sensitive on purpose: "search", "arch" and "Architecture" stay legal.
public partial class NoPersonalNamesInShippedTreeTests
{
    private static readonly string[] ShippedPatterns =
    [
        "src/*", "hooks/*", "skills/*", "agents/*", "commands/*", ".claude-plugin/*", ".githooks/*", ".github/*",
    ];

    [Fact]
    public void NoShippedFileNamesTheOwnerOrHisAgent()
    {
        string[] files = [.. Git("ls-files").Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(IsShipped)];
        Assert.NotEmpty(files);

        List<string> bad = [];
        foreach (string file in files)
        {
            string[] lines = File.ReadAllLines(Path.Combine(RepoPaths.Root, file));
            for (int i = 0; i < lines.Length; i++)
            {
                if (PersonalName().IsMatch(lines[i])) bad.Add($"{file}:{i + 1}: {lines[i].Trim()}");
            }
        }

        Assert.True(bad.Count == 0, "a personal name is still in the shipped tree:\n" + string.Join("\n", bad));
    }

    private static bool IsShipped(string file)
    {
        if (ShippedPatterns.Any(p => file.StartsWith(p[..^1], StringComparison.Ordinal))) return true;
        // root scripts and the root C# entry points (grimora.cs, mcp.cs, bootstrap.cs)
        return !file.Contains('/') && (file.EndsWith(".ps1", StringComparison.Ordinal)
            || file.EndsWith(".sh", StringComparison.Ordinal)
            || file.EndsWith(".cs", StringComparison.Ordinal));
    }

    [GeneratedRegex(@"\bStoney\b|\bArc\b", RegexOptions.None, RegexTimeout.Milliseconds)]
    private static partial Regex PersonalName();

    private static string Git(string args)
    {
        using Process p = Process.Start(new ProcessStartInfo("git", $"-C \"{RepoPaths.Root}\" {args}")
            { RedirectStandardOutput = true, UseShellExecute = false })!;
        string o = p.StandardOutput.ReadToEnd().Replace("\r", "");
        p.WaitForExit();
        return o;
    }
}
