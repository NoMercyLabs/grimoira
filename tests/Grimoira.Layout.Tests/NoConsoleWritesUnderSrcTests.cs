using Grimoira.Store.Data;
using System.Text.RegularExpressions;
using Xunit;

namespace Grimoira.Layout.Tests;

// RESTRUCTURE.md slice 29a: "CLI output goes to a writer, not the console." Slice 29 part 1 found this
// by grepping Console.Write/Console.Out/Console.Error across src/ (17 files) — a request handled inside
// the server (29c) cannot let a tool print straight to the server process's own console; the caller has
// to get the bytes back some other way. This guard freezes that grep as a test: a file under src/ that
// newly starts calling Console fails here, and every name already on the allow-list below carries the
// one-line reason it is still there instead of being fixed by this slice.
public partial class NoConsoleWritesUnderSrcTests
{

    // Path (relative to src/, forward slashes) -> why this file may still name Console.
    private static readonly Dictionary<string, string> AllowList = new(StringComparer.Ordinal)
    {
        ["Grimoira.Cli/Program.cs"] = "the CLI host's own entry point: hook and server-headers handlers read " +
            "stdin/write stdout directly, and the dispatch below passes Console.Out/Console.Error into the " +
            "tools it calls — this is the one place those literals are expected to live.",
        ["Grimoira.Server/Data/Program.cs"] = "single-instance-lock startup failure, before Kestrel builds and " +
            "before any request can be handled; tied to the Environment.Exit(1) two lines below, which " +
            "slice 29b (not this one) replaces.",
        ["Grimoira.Brain/Data/BrainWriters.cs"] = "AddTriple's stderr parameter defaults to Console.Error so " +
            "callers not yet threaded to a writer (brain learn/flush/distill) keep printing exactly as " +
            "before; BrainLearnBatchTool and SpineImportTool now pass their own writer explicitly.",
        ["Grimoira.Brain/Tools/BrainLearnBatchTool.cs"] = "ExecuteCli's new stderr parameter defaults to " +
            "Console.Error so its one direct caller (grimoira.cs) keeps today's behaviour unchanged.",
        ["Grimoira.Brain/Tools/SpineImportTool.cs"] = "ExecuteCli's new stderr parameter defaults to " +
            "Console.Error so its callers (grimoira.cs, BrainSeedTool) keep today's behaviour unchanged.",
        ["Grimoira.Server/Data/IndexJobQueue.cs"] = "the SessionEnd index queue's own background loop " +
            "(RunAsync), not a request handler: there is no caller-supplied writer to report to when the " +
            "project store itself cannot be opened, or when a handler's own failure cannot even be recorded " +
            "as a finding. Console.Error is the last resort after the finding-log write already failed.",
        // The following files never call Console at runtime — each match below is inside an XML doc
        // comment or a // comment explaining how the *old* grimoira.cs printed a row, kept for the reader
        // tracing behaviour back to its oracle. Slice 29 part 1's grep does not distinguish code from
        // comments, so these landed in its "17 files" count too.
        ["Grimoira.Brain/Tools/BrainAuditTool.cs"] = "comment only: explains the old grimoira.cs row-printing this tool's return value replaces.",
        ["Grimoira.Brain/Tools/BrainCommonTool.cs"] = "comment only: explains the old grimoira.cs row-printing this tool's return value replaces.",
        ["Grimoira.Brain/Tools/BrainCoreTool.cs"] = "comment only: explains the old grimoira.cs row-printing this tool's return value replaces.",
        ["Grimoira.Brain/Tools/BrainGapsTool.cs"] = "comment only: explains the old grimoira.cs row-printing this tool's return value replaces.",
        ["Grimoira.Brain/Tools/BrainPlaceTool.cs"] = "comment only: explains the old grimoira.cs row-printing this tool's return value replaces.",
        ["Grimoira.Brain/Tools/BrainScopeTool.cs"] = "comment only: explains the old grimoira.cs row-printing this tool's return value replaces.",
        ["Grimoira.Brain/Tools/BrainStaleTool.cs"] = "comment only: explains the old grimoira.cs row-printing this tool's return value replaces.",
        ["Grimoira.Brain/Tools/BrainWhyTool.cs"] = "comment only: explains the old grimoira.cs row-printing this tool's return value replaces.",
        ["Grimoira.Brain/Tools/EvalTool.cs"] = "comment only: explains the old grimoira.cs row-printing this tool's return value replaces.",
        ["Grimoira.Docs/Tools/DocTool.cs"] = "comment only: explains the old grimoira.cs row-printing this tool's return value replaces.",
        ["Grimoira.Memory/Tools/MemTool.cs"] = "comment only: explains the old grimoira.cs row-printing this tool's return value replaces.",
        ["Grimoira.Memory/Tools/RecallTool.cs"] = "comment only: explains the old grimoira.cs row-printing this tool's return value replaces.",
    };

    [Fact]
    public void NoNewFileUnderSrcNamesConsoleOutsideTheAllowList()
    {
        string srcRoot = Path.Combine(RepoPaths.Root, "src");
        List<string> offenders = [];
        foreach (string path in Directory.EnumerateFiles(srcRoot, "*.cs", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(srcRoot, path).Replace('\\', '/');
            if (AllowList.ContainsKey(relative)) continue;
            if (ConsoleUsage().IsMatch(File.ReadAllText(path))) offenders.Add(relative);
        }

        Assert.True(offenders.Count == 0,
            "file(s) under src/ name Console.Write/Console.Out/Console.Error and are not on the allow-list "
            + "(take a TextWriter instead, or add the name to NoConsoleWritesUnderSrcTests.AllowList with a reason):\n"
            + string.Join("\n", offenders));
    }

    [Fact]
    public void EveryAllowListEntryStillExistsAndStillMatches()
    {
        string srcRoot = Path.Combine(RepoPaths.Root, "src");
        List<string> stale = [];
        foreach (string relative in AllowList.Keys)
        {
            string full = Path.Combine(srcRoot, relative.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(full) || !ConsoleUsage().IsMatch(File.ReadAllText(full))) stale.Add(relative);
        }

        Assert.True(stale.Count == 0,
            "NoConsoleWritesUnderSrcTests.AllowList names a file that no longer exists or no longer names "
            + "Console — shrink the list:\n" + string.Join("\n", stale));
    }

    [GeneratedRegex(@"Console\.(Write\w*|Out|Error)\b", RegexOptions.None, RegexTimeout.Milliseconds)]
    private static partial Regex ConsoleUsage();
}
