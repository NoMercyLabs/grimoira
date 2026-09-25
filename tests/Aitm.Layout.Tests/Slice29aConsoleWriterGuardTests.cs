using System.Text.RegularExpressions;
using Xunit;

namespace Aitm.Layout.Tests;

// RESTRUCTURE.md slice 29a: "CLI output goes to a writer, not the console." Slice 29 part 1 found this
// by grepping Console.Write/Console.Out/Console.Error across src/ (17 files) — a request handled inside
// the server (29c) cannot let a tool print straight to the server process's own console; the caller has
// to get the bytes back some other way. This guard freezes that grep as a test: a file under src/ that
// newly starts calling Console fails here, and every name already on the allow-list below carries the
// one-line reason it is still there instead of being fixed by this slice.
public class Slice29aConsoleWriterGuardTests
{
    private static readonly Regex ConsoleUsage = new(@"Console\.(Write\w*|Out|Error)\b", RegexOptions.Compiled);

    // Path (relative to src/, forward slashes) -> why this file may still name Console.
    private static readonly Dictionary<string, string> AllowList = new(StringComparer.Ordinal)
    {
        ["Aitm.Cli/Program.cs"] = "the CLI host's own entry point: hook and server-headers handlers read " +
            "stdin/write stdout directly, and the dispatch below passes Console.Out/Console.Error into the " +
            "tools it calls — this is the one place those literals are expected to live.",
        ["Aitm.Server/Data/Program.cs"] = "single-instance-lock startup failure, before Kestrel builds and " +
            "before any request can be handled; tied to the Environment.Exit(1) two lines below, which " +
            "slice 29b (not this one) replaces.",
        // The following files never call Console at runtime — each match below is inside an XML doc
        // comment or a // comment explaining how the *old* aitm.cs printed a row, kept for the reader
        // tracing behaviour back to its oracle. Slice 29 part 1's grep does not distinguish code from
        // comments, so these landed in its "17 files" count too.
        ["Aitm.Brain/Tools/BrainAuditTool.cs"] = "comment only: explains the old aitm.cs row-printing this tool's return value replaces.",
        ["Aitm.Brain/Tools/BrainCommonTool.cs"] = "comment only: explains the old aitm.cs row-printing this tool's return value replaces.",
        ["Aitm.Brain/Tools/BrainCoreTool.cs"] = "comment only: explains the old aitm.cs row-printing this tool's return value replaces.",
        ["Aitm.Brain/Tools/BrainGapsTool.cs"] = "comment only: explains the old aitm.cs row-printing this tool's return value replaces.",
        ["Aitm.Brain/Tools/BrainPlaceTool.cs"] = "comment only: explains the old aitm.cs row-printing this tool's return value replaces.",
        ["Aitm.Brain/Tools/BrainScopeTool.cs"] = "comment only: explains the old aitm.cs row-printing this tool's return value replaces.",
        ["Aitm.Brain/Tools/BrainStaleTool.cs"] = "comment only: explains the old aitm.cs row-printing this tool's return value replaces.",
        ["Aitm.Brain/Tools/BrainWhyTool.cs"] = "comment only: explains the old aitm.cs row-printing this tool's return value replaces.",
        ["Aitm.Brain/Tools/EvalTool.cs"] = "comment only: explains the old aitm.cs row-printing this tool's return value replaces.",
        ["Aitm.Docs/Tools/DocTool.cs"] = "comment only: explains the old aitm.cs row-printing this tool's return value replaces.",
        ["Aitm.Memory/Tools/MemTool.cs"] = "comment only: explains the old aitm.cs row-printing this tool's return value replaces.",
        ["Aitm.Memory/Tools/RecallTool.cs"] = "comment only: explains the old aitm.cs row-printing this tool's return value replaces.",
    };

    [Fact]
    public void NoNewFileUnderSrcNamesConsoleOutsideTheAllowList()
    {
        string srcRoot = Path.Combine(RepoPaths.Root, "src");
        List<string> offenders = new();
        foreach (string path in Directory.EnumerateFiles(srcRoot, "*.cs", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(srcRoot, path).Replace('\\', '/');
            if (AllowList.ContainsKey(relative)) continue;
            if (ConsoleUsage.IsMatch(File.ReadAllText(path))) offenders.Add(relative);
        }

        Assert.True(offenders.Count == 0,
            "file(s) under src/ name Console.Write/Console.Out/Console.Error and are not on the allow-list "
            + "(take a TextWriter instead, or add the name to Slice29aConsoleWriterGuardTests.AllowList with a reason):\n"
            + string.Join("\n", offenders));
    }

    [Fact]
    public void EveryAllowListEntryStillExistsAndStillMatches()
    {
        string srcRoot = Path.Combine(RepoPaths.Root, "src");
        List<string> stale = new();
        foreach (string relative in AllowList.Keys)
        {
            string full = Path.Combine(srcRoot, relative.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(full) || !ConsoleUsage.IsMatch(File.ReadAllText(full))) stale.Add(relative);
        }

        Assert.True(stale.Count == 0,
            "Slice29aConsoleWriterGuardTests.AllowList names a file that no longer exists or no longer names "
            + "Console — shrink the list:\n" + string.Join("\n", stale));
    }
}
