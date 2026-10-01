using System.Text.RegularExpressions;
using Xunit;

namespace Grimoira.Layout.Tests;

// RESTRUCTURE.md slice 33: the old hook scripts, the launcher and the login scripts are deleted, and
// nothing that runs (a script, a hook, a workflow, a test) may name them again. grimoira.cs, mcp.cs and the
// oracle harness stay until the 92 test files that run the old builds have literal expected values.
public class DeadCodeIsGoneTests
{
    private static readonly string[] DeletedFiles =
    [
        "launch-mcp.mjs", "launch-mcp.test.mjs", "process-owner.mjs", "process-owner.test.mjs",
        "ranking-agreement.test.mjs", "mcp-graph.test.mjs", "mcp-stage.test.mjs",
        "brain-path.mjs", "compact-brief.mjs", "compact-restore.mjs", "pattern-watch.mjs", "pattern-watch.test.mjs",
        "session-index.mjs", "session-index-docs.mjs", "index-infra.mjs", "index-org.mjs", "hook-probe.mjs",
        "cli-exit.test.mjs", "workspace-tools.test.mjs",
        "keycloak-approve-device.mjs", "keycloak-enable-exchange.mjs", "keycloak-login-kmp.mjs",
        "keycloak-login-web.mjs", "keycloak-session.mjs",
        "tests/Grimoira.Server.Tests/BinCliThinClientMatchesBinCliOldTests.cs",
    ];

    // Names a running thing must not contain. "grimoira.cs" is matched on a word edge so "grimoira.csproj" passes.
    private static readonly Regex Named = new(
        @"launch-mcp|workspace-tools\.test|BinCliThinClientMatchesBinCliOld|ranking-agreement|mcp-graph\.test|mcp-stage\.test"
        + "|keycloak-(approve-device|enable-exchange|login-kmp|login-web|session)"
        + @"|process-owner\.mjs|(compact-brief|compact-restore|pattern-watch|session-index|index-infra|index-org|hook-probe|brain-path)\.mjs",
        RegexOptions.None, TimeSpan.FromSeconds(2));

    [Fact]
    public void DeletedFilesAreGone()
    {
        string[] present = [.. DeletedFiles.Where(f => File.Exists(Path.Combine(RepoPaths.Root, f)))];
        Assert.True(present.Length == 0, "deleted files still on disk:/n" + string.Join('\n', present));
    }

    [Fact]
    public void NoScriptHookWorkflowOrTestNamesThem()
    {
        string self = Path.Combine(RepoPaths.Root, "tests", "Grimoira.Layout.Tests", "DeadCodeIsGoneTests.cs");
        List<string> files = [];
        foreach (string pattern in new[] { "*.ps1", "*.mjs", "*.json", "*.props", ".gitignore", "*.yml" })
            files.AddRange(Directory.EnumerateFiles(RepoPaths.Root, pattern, SearchOption.TopDirectoryOnly));
        files.AddRange(Directory.EnumerateFiles(Path.Combine(RepoPaths.Root, ".github"), "*.yml", SearchOption.AllDirectories));
        files.AddRange(Directory.EnumerateFiles(Path.Combine(RepoPaths.Root, "hooks"), "*.json"));
        files.AddRange(Directory.EnumerateFiles(Path.Combine(RepoPaths.Root, "tests"), "*.cs*", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")));

        List<string> hits = [];
        foreach (string file in files.Distinct().Where(f => f != self))
        {
            foreach ((string line, int no) in File.ReadLines(file).Select((l, i) => (l, i + 1)))
            {
                if (IsProse(file, line)) continue;
                Match m = Named.Match(line);
                if (m.Success) hits.Add($"{Path.GetRelativePath(RepoPaths.Root, file)}:{no}: {m.Value}");
            }
        }
        Assert.True(hits.Count == 0, $"{hits.Count} names of deleted code:/n" + string.Join('\n', hits.Take(60)));
    }

    // A comment line is history, not a running name; the doc-comment pass owns those.
    private static bool IsProse(string file, string line)
    {
        string trimmed = line.TrimStart();
        return (file.EndsWith(".cs", StringComparison.Ordinal) || file.EndsWith(".mjs", StringComparison.Ordinal))
                && trimmed.StartsWith("//", StringComparison.Ordinal)
            || file.EndsWith(".ps1", StringComparison.Ordinal) && trimmed.StartsWith('#')
            || file.EndsWith(".yml", StringComparison.Ordinal) && trimmed.StartsWith('#');
    }
}
