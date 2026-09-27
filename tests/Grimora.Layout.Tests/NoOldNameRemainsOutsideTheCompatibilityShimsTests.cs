using System.Diagnostics;
using Xunit;

namespace Grimora.Layout.Tests;

// RESTRUCTURE.md slice 32c: the product is Grimora. The old name may survive only where it is the
// point: the shims that still read the old env vars and the old store folder, and the history text
// that narrates the rename. Every entry below names the reason.
public sealed class NoOldNameRemainsOutsideTheCompatibilityShimsTests
{
    private static readonly Dictionary<string, string> AllowList = new(StringComparer.Ordinal)
    {
        ["src/Grimora.Store/Data/LegacyEnvironment.cs"] = "compat shim: still reads the old env vars for one release.",
        ["src/Grimora.Store/Data/LegacyStore.cs"] = "compat shim: moves the old store folder once.",
        ["src/Grimora.Cli/Tools/LegacyEnvironment.cs"] = "compat shim: the CLI cannot reference Store, so it keeps its own copy.",
        ["legacy-env.mjs"] = "compat shim for the plugin scripts: old env vars and old store folder.",
        ["tests/Grimora.Layout.Tests/LegacyCompatTests.cs"] = "tests the shims, so it names the old variables and folder.",
        ["tests/Grimora.Layout.Tests/NoOldNameRemainsOutsideTheCompatibilityShimsTests.cs"] = "this guard names the old name.",
        ["tests/Grimora.Layout.Tests/CliFlagCoverageGuardTests.cs"] = "reads the pinned oracle commit aitm.cs.",
        ["tests/Grimora.TestSupport/OldStore.cs"] = "hands the pinned old binaries their old store folder and file names.",
        ["tests/Grimora.TestSupport/OldVsNewCli.cs"] = "builds the pinned oracle commit, whose file and dll are still aitm.cs and aitm.dll.",
        ["tests/Grimora.TestSupport/McpSnapshotHarness.cs"] = "the pinned snapshot reads the AITM_INSTANCE env var.",
        ["tests/Grimora.Store.Tests/StatsToolTests.cs"] = "the pinned tok.cs oracle reads the old store folder.",
        ["docs/RESTRUCTURE.md"] = "the plan narrates the rename in its history text.",
        ["docs/PLAN.md"] = "quotes the owner (2026-09-25) word for word; a quote keeps the name it was said with.",
    };

    [Fact]
    public void NoOldNameRemainsOutsideTheCompatibilityShims()
    {
        string[] files = Git("ls-files").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        List<string> offenders = [];
        foreach (string file in files)
        {
            if (AllowList.ContainsKey(file)) continue;
            if (file.Contains("aitm", StringComparison.OrdinalIgnoreCase)) { offenders.Add(file + " (path)"); continue; }
            string full = Path.Combine(RepoPaths.Root, file);
            if (!File.Exists(full)) continue;
            byte[] bytes = File.ReadAllBytes(full);
            if (System.Text.Encoding.Latin1.GetString(bytes).Contains("aitm", StringComparison.OrdinalIgnoreCase))
                offenders.Add(file);
        }
        Assert.True(offenders.Count == 0, "old name found in:/n" + string.Join('\n', offenders));
    }

    private static string Git(string args)
    {
        using Process p = Process.Start(new ProcessStartInfo("git", $"-C \"{RepoPaths.Root}\" {args}")
            { RedirectStandardOutput = true, UseShellExecute = false })!;
        string o = p.StandardOutput.ReadToEnd().Replace("\r", "");
        p.WaitForExit();
        return o;
    }
}
