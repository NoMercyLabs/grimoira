using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Grimora.TestSupport;

/// <summary>
/// RESTRUCTURE.md slice 24 ("grimora.cs and mcp.cs now hold only dispatch to the registry") moves in
/// parts, each part rewiring a handful of CLI verb cases from inline logic to a call into their tool
/// class. This is the oracle every part compares against: <see cref="OracleCommit"/> is the commit
/// slice 24 started from, so building grimora.cs exactly as it stood there gives a frozen "before" binary.
/// A test runs one verb on a fresh <c>test-*</c> instance against that frozen binary and against
/// the kept grimora.cs build, <c>bin-cli-old/grimora.dll</c>, and hands back both outputs — stdout, stderr and exit code all have
/// to match, byte for byte, or the rewiring changed behaviour.
///
/// The oracle build is cached under the OS temp dir, keyed by the commit sha, and built once per
/// machine (a lock file guards a concurrent build from two test runs racing each other). Each part adds
/// its own verbs to its own test file; none of them touch this class.
/// </summary>
public static class OldVsNewCli
{
    // The commit grimora.cs stood at when slice 24 began (RESTRUCTURE.md:523) — last touched by 9b94e7c,
    // one commit before this. Every part of the slice builds its "before" binary from here, so a part
    // that lands after another still compares against the same untouched starting point.
    public const string OracleCommit = "bbb9b4d2f4788d3f1960438799331d57198c9fbe";

    private static readonly string RepoRoot = FindRepoRoot();
    private static readonly string SnapshotDir =
        Path.Combine(Path.GetTempPath(), "grimora-slice24-oracle-" + OracleCommit[..12]);
    private static readonly Lock BuildLock = new();

    public readonly record struct Result(string Stdout, string Stderr, int ExitCode);

    /// <summary>The frozen "before" binary's path, building it first if this is the first call on this
    /// machine.</summary>
    public static string OracleDll()
    {
        string dll = Path.Combine(SnapshotDir, "aitm.dll");
        string stamp = Path.Combine(SnapshotDir, ".built-ok");
        if (File.Exists(dll) && File.Exists(stamp)) return dll;
        lock (BuildLock)
        {
            if (File.Exists(dll) && File.Exists(stamp)) return dll;
            return BuildOracle();
        }
    }

    /// <summary>Today's compiled CLI — the "after" binary, rebuilt by build-cli.ps1 after each part's
    /// changes to grimora.cs.</summary>
    public static string BinCliDll() => FindAbove(RepoRoot, Path.Combine("bin-cli-old", "grimora.dll"));

    /// <summary>Runs the same verb/arguments on a fresh <c>test-*</c> instance for each binary and
    /// returns both results, so a single command's old-vs-new behaviour is one assertion away.</summary>
    public static (Result oldResult, Result newResult) RunBoth(string arguments)
    {
        string oldInstance = GrimoraCliRunner.NewTestInstance("oracle-old");
        string newInstance = GrimoraCliRunner.NewTestInstance("oracle-new");
        try
        {
            return (Run(OracleDll(), oldInstance, arguments), Run(BinCliDll(), newInstance, arguments));
        }
        finally
        {
            GrimoraCliRunner.DeleteInstance(oldInstance);
            GrimoraCliRunner.DeleteInstance(newInstance);
        }
    }

    /// <summary>Runs one command against one binary on one instance. Exposed so a test can seed a store
    /// (e.g. <c>add</c> a fact) identically on both instances before comparing a later verb.</summary>
    public static Result Run(string dllPath, string instance, string arguments)
    {
        // The verb has to be argv[0] (grimora.cs reads `a[0]` as the command), so --instance goes after it.
        ProcessStartInfo psi = new("dotnet", $"\"{dllPath}\" {arguments} --instance {instance}")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        // The pinned oracle (aitm.dll) still keeps its store under C:/Users/dev/.aitm; hand it the instance and take it back.
        bool oracle = Path.GetFileName(dllPath) == "aitm.dll";
        if (oracle) OldStore.ToOld(instance);
        using Process process = Process.Start(psi) ?? throw new InvalidOperationException($"could not start dotnet {dllPath}");
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (oracle)
        {
            OldStore.FromOld(instance);
            // The oracle names its store paths the old way; say them the new way so the outputs compare.
            stdout = OldStore.AsNew(stdout);
            stderr = OldStore.AsNew(stderr);
        }
        return new Result(stdout, stderr, process.ExitCode);
    }

    // Another test run (a parallel agent in another worktree) may build the same oracle at the same
    // time. Build in a private folder of our own and move it into place in one step, so no run ever
    // reads a half-built snapshot — the loser of the race uses the winner's copy instead. Same fix as
    // McpSnapshotHarness.EnsureBuilt.
    private static string BuildOracle()
    {
        string buildDir = $"{SnapshotDir}.building-{Environment.ProcessId}-{Guid.NewGuid():N}";
        string worktree = Path.Combine(Path.GetTempPath(), "grimora-oracle-src-" + Guid.NewGuid().ToString("N"));
        RunOrThrow("git", $"-C \"{RepoRoot}\" worktree add \"{worktree}\" {OracleCommit} --detach");
        try
        {
            Directory.CreateDirectory(buildDir);
            RunOrThrow("dotnet", $"build \"{Path.Combine(worktree, "aitm.cs")}\" -c Release -o \"{buildDir}\"");
        }
        finally
        {
            // Always release the worktree, even if the build failed — an orphaned entry would block a
            // later build that reuses the same OracleCommit checkout path.
            try { RunOrThrow("git", $"-C \"{RepoRoot}\" worktree remove \"{worktree}\" --force"); }
            catch (InvalidOperationException) { /* best effort */ }
            try { RunOrThrow("git", $"-C \"{RepoRoot}\" worktree prune"); }
            catch (InvalidOperationException) { /* best effort */ }
        }

        string builtDll = Path.Combine(buildDir, "aitm.dll");
        if (!File.Exists(builtDll))
            throw new InvalidOperationException($"oracle build did not produce {builtDll}");
        File.WriteAllText(Path.Combine(buildDir, ".built-ok"), DateTime.UtcNow.ToString("o"));

        return MoveIntoPlace(buildDir);
    }

    private static string MoveIntoPlace(string builtDir)
    {
        string finalDll = Path.Combine(SnapshotDir, "aitm.dll");
        string finalStamp = Path.Combine(SnapshotDir, ".built-ok");
        // A final folder without the stamp is a leftover of an old in-place build; builds now happen
        // only in private folders, so nobody else is writing it.
        if (Directory.Exists(SnapshotDir) && !File.Exists(finalStamp)) TryDelete(SnapshotDir);
        try
        {
            Directory.Move(builtDir, SnapshotDir);
        }
        catch (IOException) when (File.Exists(finalStamp))
        {
            TryDelete(builtDir); // another run won the race; its copy is complete
        }
        return finalDll;
    }

    private static void TryDelete(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
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
        // this file lives at <root>/tests/Grimora.TestSupport/OldVsNewCli.cs
        string dir = Path.GetDirectoryName(here)!;
        return Path.GetFullPath(Path.Combine(dir, "..", ".."));
    }
}
