using Grimora.Store.Data;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Xunit;

namespace Grimora.Layout.Tests;

// the owner: "purge all .mjs". The plugin needs only `dotnet`. No Node script is tracked, and nothing that ships
// or gates the plugin runs `node`, and the README does not ask the user to install it.
public partial class NoNodeRemainsTests
{
    private static string[] Tracked(params string[] patterns)
    {
        ProcessStartInfo info = new("git") { RedirectStandardOutput = true, WorkingDirectory = RepoPaths.Root };
        info.ArgumentList.Add("ls-files");
        foreach (string pattern in patterns)
        {
            info.ArgumentList.Add(pattern);
        }

        using Process process = Process.Start(info)!;
        string stdout = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    [Fact]
    public void NoNodeScriptIsTracked()
    {
        string[] stray = Tracked("*.mjs", "*.js", "*.cjs");
        Assert.True(stray.Length == 0, "tracked Node files: " + string.Join(", ", stray));
    }

    [Fact]
    public void NothingThatShipsOrGatesRunsNode()
    {
        string[] files = [.. Tracked(".github/*", "hooks/*", "*.ps1", "*.json", "*.yml", ".mcp.json", "*.cs")
            .Where(f => !f.StartsWith("src/", StringComparison.Ordinal) && !f.StartsWith("tests/", StringComparison.Ordinal))];
        List<string> bad = [];
        foreach (string file in files)
        {
            string text = File.ReadAllText(Path.Combine(RepoPaths.Root, file));
            foreach (Match m in NodeUse().Matches(text))
            {
                bad.Add($"{file}: {m.Value.Trim()}");
            }
        }

        Assert.True(bad.Count == 0, "Node is still used: " + string.Join("; ", bad));
    }

    [Fact]
    public void TheReadmeDoesNotRequireNode()
    {
        string readme = File.ReadAllText(Path.Combine(RepoPaths.Root, "README.md"));
        Assert.DoesNotMatch(NodeRequirement(), readme);
    }

    // A `node` command, setup-node, or a .mjs script name.
    [GeneratedRegex(@"(?m)(""command"":\s*""node""|^\s*node\s|\bnode\s+(--|-p|-e)|setup-node|\b[\w\-]+\.mjs\b)", RegexOptions.None, RegexTimeout.Milliseconds)]
    private static partial Regex NodeUse();

    [GeneratedRegex(@"Node\.js|\bNode\b\s*\d|\.mjs", RegexOptions.None, RegexTimeout.Milliseconds)]
    private static partial Regex NodeRequirement();
}
