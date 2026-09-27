using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Grimora.TestSupport;

/// <summary>
/// RESTRUCTURE.md slice 24 ("grimora.cs and mcp.cs now hold only dispatch to the registry") moved in
/// parts, each part rewiring a handful of CLI verb cases from inline logic to a call into their tool
/// class. This is the oracle every part compared against: <see cref="OracleCommit"/> is the commit
/// slice 24 started from, so building grimora.cs exactly as it stood there gives a frozen "before" binary
/// — built from git history (<c>git show</c>/<c>git worktree</c> at that commit), independent of today's
/// working tree. A class this has already frozen a golden for (see <see cref="CliGoldens"/>) replays that
/// golden instead of running either binary; the two still had to match byte for byte, stdout, stderr and
/// exit code, when it was frozen. A class with no golden yet still runs the pinned commit's build live
/// against today's published thin client, <c>bin-cli/grimora.dll</c> (<see cref="BinCliDll"/>) — used by
/// tests that compare against schema or need a real process, not a frozen answer (e.g. InitFullTests).
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
    public static string OracleDll([System.Runtime.CompilerServices.CallerFilePath] string callerFile = "", [System.Runtime.CompilerServices.CallerMemberName] string callerMember = "")
    {
        // A class that has been frozen never builds the oracle; the sentinel only says "the frozen oracle".
        if (!CliGoldens.FreezeMode && CliGoldens.HasGolden(callerFile)) return Path.Combine(SnapshotDir, "aitm.dll");
        string dll = Path.Combine(SnapshotDir, "aitm.dll");
        string stamp = Path.Combine(SnapshotDir, ".built-ok");
        if (File.Exists(dll) && File.Exists(stamp)) return dll;
        lock (BuildLock)
        {
            if (File.Exists(dll) && File.Exists(stamp)) return dll;
            return BuildOracle();
        }
    }

    /// <summary>Today's compiled CLI — the published thin client, rebuilt by build-cli.ps1/.sh.</summary>
    public static string BinCliDll([System.Runtime.CompilerServices.CallerFilePath] string callerFile = "", [System.Runtime.CompilerServices.CallerMemberName] string callerMember = "") =>
        !CliGoldens.FreezeMode && CliGoldens.HasGolden(callerFile)
            ? InProcessDll
            : FindAbove(RepoRoot, Path.Combine("bin-cli", "grimora.dll"));

    /// <summary>The sentinel for "the current code, run in-process" once a class is frozen.</summary>
    public const string InProcessDll = "in-process/grimora.dll";

    /// <summary>Runs the same verb/arguments on a fresh <c>test-*</c> instance for each binary and
    /// returns both results, so a single command's old-vs-new behaviour is one assertion away.</summary>
    public static (Result oldResult, Result newResult) RunBoth(string arguments, [System.Runtime.CompilerServices.CallerFilePath] string callerFile = "", [System.Runtime.CompilerServices.CallerMemberName] string callerMember = "")
    {
        string oldInstance = GrimoraCliRunner.NewTestInstance("oracle-old");
        string newInstance = GrimoraCliRunner.NewTestInstance("oracle-new");
        try
        {
            return (Run(OracleDll(callerFile, callerMember), oldInstance, arguments, callerFile, callerMember),
                Run(BinCliDll(callerFile, callerMember), newInstance, arguments, callerFile, callerMember));
        }
        finally
        {
            GrimoraCliRunner.DeleteInstance(oldInstance);
            GrimoraCliRunner.DeleteInstance(newInstance);
        }
    }

    /// <summary>Runs one command against one binary on one instance. Exposed so a test can seed a store
    /// (e.g. <c>add</c> a fact) identically on both instances before comparing a later verb.</summary>
    public static Result Run(string dllPath, string instance, string arguments, [System.Runtime.CompilerServices.CallerFilePath] string callerFile = "", [System.Runtime.CompilerServices.CallerMemberName] string callerMember = "")
    {
        bool frozenClass = !CliGoldens.FreezeMode && CliGoldens.HasGolden(callerFile);
        if (frozenClass && dllPath == InProcessDll) return RunInProcess(instance, arguments);
        if (frozenClass && Path.GetFileName(dllPath) == "aitm.dll") return Replay(instance, arguments, callerFile, callerMember);
        Result live = RunLive(dllPath, instance, arguments);
        if (CliGoldens.FreezeMode && Path.GetFileName(dllPath) == "aitm.dll")
        {
            CliGoldens.Record(callerFile, callerMember, arguments, live.Stdout, live.Stderr, live.ExitCode, instance, PinnedHeader);
        }
        return live;
    }

    private const string PinnedHeader =
        "GOLDEN written by the pinned oracle aitm.cs at commit bbb9b4d2f4788d3f1960438799331d57198c9fbe (slice 24 start); never by the new code";

    private static Result Replay(string instance, string arguments, string callerFile, string callerMember)
    {
        CliGoldens.Entry golden = CliGoldens.Take(callerFile, callerMember, arguments);
        // The answer is the golden; the command also runs in-process on the oracle's instance so the store
        // a later step of the test reads exists (a test that reads that store compares the current code with itself there).
        RunInProcess(instance, arguments);
        return new Result(
            CliGoldens.ForThisRun(golden, golden.Stdout, arguments, instance),
            CliGoldens.ForThisRun(golden, golden.Stderr, arguments, instance),
            golden.ExitCode);
    }

    private static Result RunInProcess(string instance, string arguments)
    {
        using StringWriter stdout = new();
        using StringWriter stderr = new();
        int exit = Grimora.Server.Data.CliDispatch.Run(
            CliGoldens.SplitArguments($"{arguments} --instance {instance}"), Directory.GetCurrentDirectory(), stdout, stderr);
        return new Result(stdout.ToString(), stderr.ToString(), exit);
    }

    private static Result RunLive(string dllPath, string instance, string arguments)
    {
        if (OperatingSystem.IsWindows()) SetConsoleOutputCP(65001);
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

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleOutputCP(uint wCodePageId);

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
