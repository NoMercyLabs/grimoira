using System.Diagnostics;
using Xunit;

namespace Grimoira.Layout.Tests;

// The product is Grimoira. The name before it, Grimora, may survive only where it is the point: the shims that
// still read the old env vars and the old store folder, the old logon leftovers the code removes, the legacy
// stub plugin that tells a user how to move over, and the text that tells a returning user about the rename.
// Every entry below names the reason.
public sealed class NoPreviousProductNameRemainsOutsideTheAllowListTests
{
    private static readonly Dictionary<string, string> AllowList = new(StringComparer.Ordinal)
    {
        ["src/Grimoira.Store/Data/LegacyEnvironment.cs"] = "compat shim: still reads GRIMORA_* for one release.",
        ["src/Grimoira.Store/Data/LegacyStore.cs"] = "compat shim: copies the old C:/Users/patri/.grimora store once.",
        ["src/Grimoira.Cli/Tools/LegacyEnvironment.cs"] = "compat shim: the CLI cannot reference Store, so it keeps its own copy.",
        ["src/Grimoira.Cli/Tools/ServerLogonTool.cs"] = "removes the Grimora.Server task and the launchd label that 1.0.x installs left.",
        ["tests/Grimoira.Cli.Tests/ServerLogonCommandTests.cs"] = "proves the old task name is the one removed.",
        ["tests/Grimoira.Layout.Tests/LegacyStoreChainTests.cs"] = "tests the store chain, so it names the old folders and databases.",
        ["tests/Grimoira.Layout.Tests/LegacyEnvironmentPromotionTests.cs"] = "tests the shims, so it names the old variables.",
        ["tests/Grimoira.Layout.Tests/NoPreviousProductNameRemainsOutsideTheAllowListTests.cs"] = "this guard names the old name.",
        ["tests/Grimoira.TestSupport/CliGoldens.cs"] = "a frozen golden's header names grimora.cs at the pinned commit.",
        [".claude-plugin/marketplace.json"] = "lists the legacy stub plugin that old installs still have.",
        ["README.md"] = "tells a returning user about the rename and the store copy.",
        ["docs/ARCHITECTURE.md"] = "names the external private repository grimora-internal.",
    };

    // grimora-archive and grimora-internal are external repository names that keep their name.
    private static readonly string[] AllowedPrefixes = ["legacy/grimora/"];

    [Fact]
    public void NoPreviousProductNameRemainsOutsideTheAllowList()
    {
        string[] files = Git("ls-files").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        List<string> offenders = [];
        foreach (string file in files)
        {
            if (AllowList.ContainsKey(file)) continue;
            if (AllowedPrefixes.Any(prefix => file.StartsWith(prefix, StringComparison.Ordinal))) continue;
            // A frozen golden is a byte-for-byte capture of the oracle's own answer; CliGoldens.Portable maps
            // the old name to the current one when it compares.
            if (file.Contains("/Goldens/", StringComparison.Ordinal) && file.EndsWith(".jsonl", StringComparison.Ordinal)) continue;
            if (file.Contains("grimora", StringComparison.OrdinalIgnoreCase)) { offenders.Add(file + " (path)"); continue; }
            string full = Path.Combine(RepoPaths.Root, file);
            if (!File.Exists(full)) continue;
            string text = System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(full))
                .Replace("grimora-archive", "", StringComparison.OrdinalIgnoreCase)
                .Replace("grimora-internal", "", StringComparison.OrdinalIgnoreCase);
            if (text.Contains("grimora", StringComparison.OrdinalIgnoreCase)) offenders.Add(file);
        }
        Assert.True(offenders.Count == 0, "previous product name found in:" + Environment.NewLine + string.Join('\n', offenders));
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
