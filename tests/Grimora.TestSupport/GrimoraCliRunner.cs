using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Grimora.TestSupport;

/// <summary>
/// Runs a CLI verb against a throwaway <c>test-*</c> instance. <see cref="Run"/> replays the class's frozen
/// golden (<see cref="CliGoldens"/>) and asserts the current code, run in-process, still matches it — no
/// live oracle process runs any more, every caller has a golden. <see cref="RunBinCli"/> spawns the real
/// published <c>bin-cli/grimora.dll</c> for a test that needs a genuine separate OS process instead.
/// Previously duplicated once per test project (<c>Grimora.Store.Tests</c>, then <c>Grimora.Facts.Tests</c>)
/// because the class was internal to its own assembly; a third copy for <c>Grimora.Memory.Tests</c> would
/// have made three, so this is the one shared place instead (Grimora.Layout.Tests' folder rules only
/// govern src/*, not tests/*, so nothing there blocks the move).
/// </summary>
public static class GrimoraCliRunner
{
    // WAL-mode sqlite (StoreConnection.ApplyPragmas) can leave a native file handle on the -wal/-shm file
    // for a few hundred milliseconds after SqliteConnection.ClearAllPools() returns on Windows, so a
    // Directory.Delete right after it intermittently throws IOException/UnauthorizedAccessException. Same
    // fix as McpSnapshotHarness.TryDelete and OldVsNewCli's equivalent (both in this project), and the
    // matching Node fix in bbb9b4d (rmSync maxRetries/retryDelay) — retry instead of surfacing the race.
    private const int DeleteMaxRetries = 20;
    private const int DeleteRetryDelayMs = 250;

    // At process exit, only inspect stores created by this process. The verifier fails if one remains.
    static GrimoraCliRunner()
    {
        // Do not scan and delete other runs' test stores as a side effect of starting a test.
        // Ownership cannot be inferred from age, and locked leftovers made every test wait minutes.
        // A test that fails before its own cleanup must not leave a test-* folder in the real home.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => RemoveCreatedInstances();
    }

    private static readonly System.Collections.Concurrent.ConcurrentBag<string> Created = [];

    private static void RemoveCreatedInstances()
    {
        List<string> survivors = [];
        foreach (string instance in Created)
        {
            try { DeleteInstance(instance); }
            catch (Exception) { /* report surviving directory below */ }
            if (Directory.Exists(InstanceDir(instance))) survivors.Add(instance);
        }
        if (survivors.Count > 0)
        {
            Console.Error.WriteLine("test store cleanup failed: " + string.Join(", ", survivors));
            Environment.ExitCode = 1;
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
        if (Directory.Exists(dir))
        {
            try { DeleteDirectoryWithRetry(dir); }
            catch (IOException e) { throw new IOException($"test store cleanup failed: {dir}", e); }
        }
        OldStore.Delete(instance);
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

    /// <summary>Runs <c>grimora &lt;arguments&gt;</c> in-process and returns (stdout, exit code), after
    /// asserting the answer still matches the calling test's frozen golden (see <see cref="CliGoldens"/>);
    /// throws <see cref="CliGoldens.MismatchException"/> on a mismatch. No oracle process runs any more —
    /// every caller of this method has a golden already.</summary>
    public static (string stdout, int exitCode) Run(
        string arguments,
        [System.Runtime.CompilerServices.CallerFilePath] string callerFile = "",
        [System.Runtime.CompilerServices.CallerMemberName] string callerMember = "")
    {
        CliGoldens.Entry golden = CliGoldens.Take(callerFile, callerMember, arguments);
        (string stdout, string stderr, int exitCode) = RunCurrent(arguments);
        CliGoldens.AssertMatches(golden, arguments, stdout, stderr, exitCode);
        return (stdout, exitCode);
    }

    /// <summary>Runs <c>grimora &lt;arguments&gt;</c> in-process to build a fixture (seed a store), with no
    /// golden comparison: the call populates a store for a later comparison to read, it is not itself the
    /// behaviour under test, so there is nothing here for a golden to prove.</summary>
    public static (string stdout, int exitCode) Seed(string arguments)
    {
        (string stdout, string _, int exitCode) = RunCurrent(arguments);
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
    public static (string stdout, int exitCode) RunBinCli(string arguments, string? dataDir = null)
    {
        (string stdout, string stderr, int exitCode) = RunProcess(FindBinCliDll(), arguments, dataDir);
        if (exitCode != 0 && string.IsNullOrEmpty(stdout))
            throw new InvalidOperationException($"'{arguments}' exited {exitCode}\nSTDERR:\n{stderr}");
        return (stdout, exitCode);
    }

    private static (string stdout, string stderr, int exitCode) RunProcess(string dll, string arguments, string? dataDir)
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
        if (dataDir is not null) psi.Environment["GRIMORA_DATA_DIR"] = dataDir;
        using Process process = Process.Start(psi) ?? throw new InvalidOperationException($"could not start dotnet {dll}");
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (stdout, stderr, process.ExitCode);
    }

    private const uint Utf8CodePage = 65001;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleOutputCP(uint wCodePageId);

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
