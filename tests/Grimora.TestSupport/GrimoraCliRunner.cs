using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Grimora.TestSupport;

/// <summary>
/// Runs today's compiled <c>bin-cli-old/grimora.dll</c> (the kept grimora.cs build) against a throwaway <c>test-*</c> instance and returns
/// its stdout — the oracle every project's pinned-output tests run against. Previously duplicated once
/// per test project (<c>Grimora.Store.Tests</c>, then <c>Grimora.Facts.Tests</c>) because the class was
/// internal to its own assembly; a third copy for <c>Grimora.Memory.Tests</c> would have made three, so
/// this is the one shared place instead (Grimora.Layout.Tests' folder rules only govern src/*, not
/// tests/*, so nothing there blocks the move).
/// </summary>
public static class GrimoraCliRunner
{
    // A test-* store older than this has no owner left: another agent's run would still be touching it
    // (writing to it, at least once, well within an hour), so anything past this is safe to sweep.
    private static readonly TimeSpan StaleInstanceAge = TimeSpan.FromHours(1);

    // WAL-mode sqlite (StoreConnection.ApplyPragmas) can leave a native file handle on the -wal/-shm file
    // for a few hundred milliseconds after SqliteConnection.ClearAllPools() returns on Windows, so a
    // Directory.Delete right after it intermittently throws IOException/UnauthorizedAccessException. Same
    // fix as McpSnapshotHarness.TryDelete and OldVsNewCli's equivalent (both in this project), and the
    // matching Node fix in bbb9b4d (rmSync maxRetries/retryDelay) — retry instead of surfacing the race.
    private const int DeleteMaxRetries = 20;
    private const int DeleteRetryDelayMs = 250;

    // Runs once per test process, the first time anything in this class is touched — effectively "at test
    // run start" for every test project that uses GrimoraCliRunner. Clears out instances no run of this class
    // still owns, so a store leaked by a lock that outlasted DeleteMaxRetries * DeleteRetryDelayMs (5s)
    // doesn't accumulate under ~/.grimora forever.
    static GrimoraCliRunner()
    {
        SweepStaleInstances();
        // A test that fails before its own cleanup must not leave a test-* folder in the real home.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => RemoveCreatedInstances();
    }

    private static readonly System.Collections.Concurrent.ConcurrentBag<string> Created = [];

    private static void RemoveCreatedInstances()
    {
        foreach (string instance in Created)
        {
            try { DeleteInstance(instance); }
            catch (Exception) { /* best effort at process exit */ }
        }
    }

    public static string NewTestInstance(string label)
    {
        string instance = $"test-{label}-{Guid.NewGuid():N}";
        Created.Add(instance);
        return instance;
    }

    public static string InstanceDbPath(string instance) => Path.Combine(InstanceDir(instance), "grimora.db");

    public static string InstanceDir(string instance) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".grimora", instance);

    public static void DeleteInstance(string instance)
    {
        if (!instance.StartsWith("test-", StringComparison.Ordinal))
            throw new InvalidOperationException($"refusing to delete '{instance}': not a test-* instance");
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        string dir = InstanceDir(instance);
        if (Directory.Exists(dir)) DeleteDirectoryWithRetry(dir);
        OldStore.Delete(instance);
    }

    /// <summary>Removes every <c>test-*</c> instance directory under <c>~/.grimora</c> that has not been
    /// written to in over an hour. Never touches anything younger — a run from another agent's worktree may
    /// still own it. Exposed (not just run from the static constructor) so a test can call it directly and
    /// assert on exactly what it does and does not remove.</summary>
    public static void SweepStaleInstances()
    {
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".grimora");
        if (!Directory.Exists(root)) return;
        DateTime cutoffUtc = DateTime.UtcNow - StaleInstanceAge;
        foreach (string dir in Directory.EnumerateDirectories(root, "test-*"))
        {
            try
            {
                if (Directory.GetLastWriteTimeUtc(dir) >= cutoffUtc) continue;
                DeleteDirectoryWithRetry(dir);
            }
            catch (IOException) { /* still locked, or another sweep/agent won the race; leave it for next time */ }
            catch (UnauthorizedAccessException) { /* same */ }
        }
    }

    private static void DeleteDirectoryWithRetry(string dir, int maxRetries = DeleteMaxRetries, int retryDelayMs = DeleteRetryDelayMs)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                Directory.Delete(dir, recursive: true);
                return;
            }
            catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) && attempt < maxRetries)
            {
                Thread.Sleep(retryDelayMs);
            }
        }
    }

    /// <summary>Runs <c>grimora &lt;arguments&gt;</c> and returns (stdout, exit code). Never throws on a
    /// non-zero exit so a caller can pin an error path too. The returned stdout is the frozen golden of the
    /// calling test (see <see cref="CliGoldens"/>); the current CLI code runs in-process and must match it,
    /// otherwise this throws <see cref="CliGoldens.MismatchException"/>. With GRIMORA_FREEZE=1 the oracle
    /// build in bin-cli-old runs instead and its answer is written to the golden.</summary>
    public static (string stdout, int exitCode) Run(
        string arguments,
        [System.Runtime.CompilerServices.CallerFilePath] string callerFile = "",
        [System.Runtime.CompilerServices.CallerMemberName] string callerMember = "")
    {
        if (CliGoldens.FreezeMode || !CliGoldens.HasGolden(callerFile))
        {
            (string oldOut, string oldErr, int oldExit) = RunOracle(arguments);
            if (CliGoldens.FreezeMode) CliGoldens.Record(callerFile, callerMember, arguments, oldOut, oldErr, oldExit);
            else if (oldExit != 0 && string.IsNullOrEmpty(oldOut))
                throw new InvalidOperationException($"'{arguments}' exited {oldExit}/nSTDERR:/n{oldErr}");
            return (oldOut, oldExit);
        }

        CliGoldens.Entry golden = CliGoldens.Take(callerFile, callerMember, arguments);
        (string stdout, string stderr, int exitCode) = RunCurrent(arguments);
        CliGoldens.AssertMatches(golden, arguments, stdout, stderr, exitCode);
        return (stdout, exitCode);
    }

    private static (string stdout, string stderr, int exitCode) RunCurrent(string arguments)
    {
        using StringWriter stdout = new();
        using StringWriter stderr = new();
        int exit = Grimora.Server.Data.CliDispatch.Run(CliGoldens.SplitArguments(arguments), Directory.GetCurrentDirectory(), stdout, stderr);
        return (stdout.ToString(), stderr.ToString(), exit);
    }

    /// <summary>Spawns a real <c>dotnet bin-cli/grimora.dll &lt;arguments&gt;</c> process — the published thin
    /// client, i.e. today's code, not the oracle. For a test that needs a genuine separate OS process (a
    /// cross-process file-lock race, for instance), not a stdout comparison: goldens replay a fixed answer,
    /// which cannot stand in for two real processes racing each other.</summary>
    public static (string stdout, int exitCode) RunBinCli(string arguments)
    {
        (string stdout, string stderr, int exitCode) = RunProcess(FindBinCliDll(), arguments);
        if (exitCode != 0 && string.IsNullOrEmpty(stdout))
            throw new InvalidOperationException($"'{arguments}' exited {exitCode}\nSTDERR:\n{stderr}");
        return (stdout, exitCode);
    }

    private static (string stdout, string stderr, int exitCode) RunOracle(string arguments) => RunProcess(FindGrimoraDll(), arguments);

    private static (string stdout, string stderr, int exitCode) RunProcess(string dll, string arguments)
    {
        // The child inherits this process's console; on Windows its output code page decides how it encodes
        // non-ASCII text, so switch it to UTF-8 first (no-op on Linux/macOS).
        if (OperatingSystem.IsWindows()) SetConsoleOutputCP(Utf8CodePage);

        ProcessStartInfo psi = new("dotnet", $"\"{dll}\" {arguments}")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        using Process process = Process.Start(psi) ?? throw new InvalidOperationException($"could not start dotnet {dll}");
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (stdout, stderr, process.ExitCode);
    }

    private const uint Utf8CodePage = 65001;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleOutputCP(uint wCodePageId);

    private static string FindGrimoraDll() => FindAbove(Path.Combine("bin-cli-old", "grimora.dll"));

    private static string FindBinCliDll() => FindAbove(Path.Combine("bin-cli", "grimora.dll"));

    private static string FindAbove(string relative)
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new InvalidOperationException($"{relative} not found above {AppContext.BaseDirectory} — run build-cli.ps1 first");
    }
}
