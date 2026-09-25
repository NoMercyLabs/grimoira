using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Aitm.TestSupport;

/// <summary>
/// RESTRUCTURE.md slice 24 ("aitm.cs and mcp.cs now hold only dispatch to the registry") moves in
/// parts, each part rewiring a handful of CLI verb cases from inline logic to a call into their tool
/// class. This is the oracle every part compares against: <see cref="OracleCommit"/> is the commit
/// slice 24 started from, so building aitm.cs exactly as it stood there gives a frozen "before" binary.
/// A test runs one verb on a fresh <c>test-*</c> instance against that frozen binary and against
/// today's <c>bin-cli/aitm.dll</c>, and hands back both outputs — stdout, stderr and exit code all have
/// to match, byte for byte, or the rewiring changed behaviour.
///
/// The oracle build is cached under the OS temp dir, keyed by the commit sha, and built once per
/// machine (a lock file guards a concurrent build from two test runs racing each other). Each part adds
/// its own verbs to its own test file; none of them touch this class.
/// </summary>
public static class OldVsNewCli
{
    // The commit aitm.cs stood at when slice 24 began (RESTRUCTURE.md:523) — last touched by 9b94e7c,
    // one commit before this. Every part of the slice builds its "before" binary from here, so a part
    // that lands after another still compares against the same untouched starting point.
    public const string OracleCommit = "bbb9b4d2f4788d3f1960438799331d57198c9fbe";

    private static readonly string RepoRoot = FindRepoRoot();
    private static readonly string SnapshotDir =
        Path.Combine(Path.GetTempPath(), "aitm-slice24-oracle-" + OracleCommit[..12]);
    private static readonly object BuildLock = new();

    public readonly record struct Result(string Stdout, string Stderr, int ExitCode);

    /// <summary>The frozen "before" binary's path, building it first if this is the first call on this
    /// machine.</summary>
    public static string OracleDll()
    {
        string dll = Path.Combine(SnapshotDir, "aitm.dll");
        if (File.Exists(dll)) return dll;
        lock (BuildLock)
        {
            if (File.Exists(dll)) return dll;
            BuildOracle(dll);
        }
        return dll;
    }

    /// <summary>Today's compiled CLI — the "after" binary, rebuilt by build-cli.ps1 after each part's
    /// changes to aitm.cs.</summary>
    public static string BinCliDll() => FindAbove(RepoRoot, Path.Combine("bin-cli", "aitm.dll"));

    /// <summary>Runs the same verb/arguments on a fresh <c>test-*</c> instance for each binary and
    /// returns both results, so a single command's old-vs-new behaviour is one assertion away.</summary>
    public static (Result oldResult, Result newResult) RunBoth(string arguments)
    {
        string oldInstance = AitmCliRunner.NewTestInstance("oracle-old");
        string newInstance = AitmCliRunner.NewTestInstance("oracle-new");
        try
        {
            return (Run(OracleDll(), oldInstance, arguments), Run(BinCliDll(), newInstance, arguments));
        }
        finally
        {
            AitmCliRunner.DeleteInstance(oldInstance);
            AitmCliRunner.DeleteInstance(newInstance);
        }
    }

    /// <summary>Runs one command against one binary on one instance. Exposed so a test can seed a store
    /// (e.g. <c>add</c> a fact) identically on both instances before comparing a later verb.</summary>
    public static Result Run(string dllPath, string instance, string arguments)
    {
        // The verb has to be argv[0] (aitm.cs reads `a[0]` as the command), so --instance goes after it.
        ProcessStartInfo psi = new("dotnet", $"\"{dllPath}\" {arguments} --instance {instance}")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        using Process process = Process.Start(psi) ?? throw new InvalidOperationException($"could not start dotnet {dllPath}");
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return new Result(stdout, stderr, process.ExitCode);
    }

    private static void BuildOracle(string dll)
    {
        string worktree = Path.Combine(Path.GetTempPath(), "aitm-oracle-src-" + Guid.NewGuid().ToString("N"));
        RunOrThrow("git", $"-C \"{RepoRoot}\" worktree add \"{worktree}\" {OracleCommit} --detach");
        try
        {
            Directory.CreateDirectory(SnapshotDir);
            RunOrThrow("dotnet", $"build \"{Path.Combine(worktree, "aitm.cs")}\" -c Release -o \"{SnapshotDir}\"");
        }
        finally
        {
            RunOrThrow("git", $"-C \"{RepoRoot}\" worktree remove \"{worktree}\" --force");
        }
        if (!File.Exists(dll))
            throw new InvalidOperationException($"oracle build did not produce {dll}");
    }

    private static void RunOrThrow(string exe, string args)
    {
        ProcessStartInfo psi = new(exe, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using Process process = Process.Start(psi) ?? throw new InvalidOperationException($"could not start {exe} {args}");
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"'{exe} {args}' exited {process.ExitCode}\nSTDOUT:\n{stdout}\nSTDERR:\n{stderr}");
    }

    private static string FindAbove(string start, string relative)
    {
        DirectoryInfo? dir = new(start);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new InvalidOperationException($"{relative} not found above {start} — run build-cli.ps1 first");
    }

    private static string FindRepoRoot([CallerFilePath] string here = "")
    {
        // this file lives at <root>/tests/Aitm.TestSupport/OldVsNewCli.cs
        string dir = Path.GetDirectoryName(here)!;
        return Path.GetFullPath(Path.Combine(dir, "..", ".."));
    }
}
