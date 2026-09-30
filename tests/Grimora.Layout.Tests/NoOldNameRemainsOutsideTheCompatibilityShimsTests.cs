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
        ["tests/Grimora.Layout.Tests/LegacyStoreMoveTests.cs"] = "tests the store move, so it names the old folder.",
        ["tests/Grimora.Layout.Tests/LegacyCompatTests.cs"] = "tests the shims, so it names the old variables and folder.",
        ["tests/Grimora.Layout.Tests/NoOldNameRemainsOutsideTheCompatibilityShimsTests.cs"] = "this guard names the old name.",
        ["tests/Grimora.Layout.Tests/CliFlagCoverageGuardTests.cs"] = "reads the pinned oracle commit aitm.cs.",
        ["tests/Grimora.TestSupport/OldStore.cs"] = "hands the pinned old binaries their old store folder and file names.",
        ["tests/Grimora.TestSupport/OldVsNewCli.cs"] = "builds the pinned oracle commit, whose file and dll are still aitm.cs and aitm.dll.",
        ["tests/Grimora.TestSupport/McpSnapshotHarness.cs"] = "the pinned snapshot reads the AITM_INSTANCE env var.",
        ["tests/Grimora.Store.Tests/StatsToolTests.cs"] = "the pinned tok.cs oracle reads the old store folder.",
        ["tests/Grimora.TestSupport/CliGoldens.cs"] = "a golden's header records the old store name for the run it was frozen from.",
        ["README.md"] = "one sentence tells a returning user that their data moves from the earlier home-folder store the first time.",
    };


    [Fact]
    public void NoOldNameRemainsOutsideTheCompatibilityShims()
    {
        string[] files = Git("ls-files").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        List<string> offenders = [];
        foreach (string file in files)
        {
            if (AllowList.ContainsKey(file)) continue;
            // A frozen golden is a literal, byte-for-byte capture of the oracle's own answer at freeze
            // time, including the store folder name the oracle actually used — CliGoldens.Header records
            // that provenance on purpose, in every project's Goldens/*.jsonl, not a lingering reference to fix.
            if (file.Contains("/Goldens/", StringComparison.Ordinal) && file.EndsWith(".jsonl", StringComparison.Ordinal)) continue;
            if (file.Contains("aitm", StringComparison.OrdinalIgnoreCase)) { offenders.Add(file + " (path)"); continue; }
            string full = Path.Combine(RepoPaths.Root, file);
            if (!File.Exists(full)) continue;
            byte[] bytes = File.ReadAllBytes(full);
            if (System.Text.Encoding.Latin1.GetString(bytes).Contains("aitm", StringComparison.OrdinalIgnoreCase))
                offenders.Add(file);
        }
        Assert.True(offenders.Count == 0, "old name found in:" + Environment.NewLine + string.Join('\n', offenders));
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
