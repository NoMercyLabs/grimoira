using System.Diagnostics;
using System.Text.RegularExpressions;
using Xunit;

namespace Grimora.Layout.Tests;

// the owner: "ensure all the mjs files are purged". Node is only the plugin launcher until a later card
// replaces it: these seven launcher scripts (and the build helpers the session-start launcher imports)
// are the whole Node surface. Everything else is C# behind the `grimora` verbs.
public partial class OnlyTheLaunchScriptsRemainAsNodeTests
{
    private static readonly HashSet<string> Keep = new(StringComparer.Ordinal)
    {
        "session-start.mjs", "session-start.test.mjs",
        "run-hook.mjs", "run-hook.test.mjs",
        "run-mcp.mjs", "run-mcp.test.mjs",
        "legacy-env.mjs",
        "build-stamp.mjs", "build-stamp.test.mjs",
        "build-cli-and-server.mjs", "published-cli.mjs",
        // Live PreToolUse hooks with no C# equivalent yet: deleting them would drop a protection the owner runs
        // on every shell and browser call. They leave with their C# port, not before.
        "shell-guard.mjs", "chrome-ready.mjs",
    };

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
    public void NoNodeScriptOutsideTheKeepListIsTracked()
    {
        string[] stray = [.. Tracked("*.mjs", "*.js", "*.cjs").Where(f => !Keep.Contains(f))];
        Assert.True(stray.Length == 0, "tracked Node files outside the keep list: " + string.Join(", ", stray));
    }

    [Fact]
    public void NothingRunsANodeScriptThatIsNotOnTheKeepList()
    {
        string[] files = Tracked(".github/*", "hooks/*", "*.ps1", "*.json", "*.yml", ".mcp.json");
        List<string> bad = [];
        foreach (string file in files.Where(f => !f.StartsWith("docs/", StringComparison.Ordinal)))
        {
            string text = File.ReadAllText(Path.Combine(RepoPaths.Root, file));
            foreach (Match m in ScriptName().Matches(text))
            {
                string name = m.Groups[1].Value;
                if (!Keep.Contains(name) && !name.Contains('*'))
                {
                    bad.Add($"{file}: {name}");
                }
            }
        }

        Assert.True(bad.Count == 0, "references to Node scripts not on the keep list: " + string.Join("; ", bad.Distinct()));
    }

    [GeneratedRegex(@"([A-Za-z0-9_\-*]+\.(?:test\.)?mjs)", RegexOptions.None, matchTimeoutMilliseconds: 2000)]
    private static partial Regex ScriptName();
}
