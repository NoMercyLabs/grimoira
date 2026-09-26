using System.Diagnostics;
using System.Text.Json;

namespace Aitm.Cli.Tools;

/// <summary>
/// RESTRUCTURE.md slice 32b: a new build's server takes over from the running one. A server keeps the build it
/// started from, so after a plugin update moves <c>current</c> the running server (the logon task's can run for
/// days) still serves the old build. On SessionStart this compares the running server's <c>/health</c>
/// <c>buildStamp</c> with the stamp of the build this CLI runs from (<c>current</c>):
/// <list type="bullet">
/// <item>Same build, no stamp on either side (a checkout or a server from before 32b): reused, as before.</item>
/// <item>Another build: <c>POST /shutdown</c> asks it to make way. It finishes its calls in flight and exits,
/// which frees its single-instance lock (<c>server.lock</c>); then the server of this build starts, through the
/// same <see cref="ServerAutoStart"/> start-and-wait as a server that is down.</item>
/// <item>A refused or failed shutdown request fails open: the old server keeps serving.</item>
/// <item>One swap at a time: <c>server-swap.lock</c> in the data folder. A second SessionStart that finds it
/// taken waits for the new server and starts nothing.</item>
/// </list>
/// Never throws; a server is never killed.
/// </summary>
public static class ServerHandover
{
    public const string SwapLockFileName = "server-swap.lock";

    /// <summary>What <c>/health</c> said: whether a server answers, and the build it reports.</summary>
    public readonly record struct Running(bool Answers, string? BuildStamp);

    /// <summary>True when, at the end, a server answers (the current build's, unless the swap failed open).</summary>
    public static bool Run(
        string? currentStamp,
        Func<Running> probe,
        Func<IDisposable?> trySwapLock,
        Func<bool> requestShutdown,
        Func<bool> oldServerGone,
        Func<bool> tryStart,
        TimeSpan maxWait,
        TimeSpan pollInterval)
    {
        try
        {
            Running now = probe();
            if (now.Answers && (currentStamp is null || now.BuildStamp is null || now.BuildStamp == currentStamp)) return true;
            if (currentStamp is null) return ServerAutoStart.EnsureRunning(() => probe().Answers, tryStart, maxWait, pollInterval);

            Stopwatch clock = Stopwatch.StartNew();
            TimeSpan Left() => maxWait - clock.Elapsed;
            bool CurrentAnswers() => probe() is { Answers: true } r && r.BuildStamp == currentStamp;

            using IDisposable? swap = trySwapLock();
            if (swap is null) return WaitFor(CurrentAnswers, Left(), pollInterval); // another SessionStart swaps

            now = probe();
            if (now.Answers)
            {
                if (now.BuildStamp is null || now.BuildStamp == currentStamp) return true;
                if (!requestShutdown()) return true; // fails open: the old server keeps serving
                // Still draining a long call when the time is up: it exits when that call ends, and the next
                // start (a SessionStart or a thin client) comes from the current build.
                if (!WaitFor(oldServerGone, Left(), pollInterval)) return false;
            }
            return ServerAutoStart.EnsureRunning(() => probe().Answers, tryStart, Left(), pollInterval);
        }
        catch
        {
            // A failed probe, lock or request leaves whatever runs alone.
            return false;
        }
    }

    /// <summary>The data folder Aitm.Server uses: <c>AITM_DATA_DIR</c>, else <c>~/.aitm</c>.</summary>
    private static string DefaultDataDir() =>
        Environment.GetEnvironmentVariable("AITM_DATA_DIR") is { Length: > 0 } configured
            ? configured
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".aitm");

    /// <summary><see cref="Run"/> on the real port, data folder, server path and the stamp of this CLI's build.</summary>
    public static bool RunDefault()
    {
        int port = ServerAutoStart.DefaultPort();
        string dataDir = DefaultDataDir();
        return Run(
            ReadStamp(AppContext.BaseDirectory),
            () => Probe(port, TimeSpan.FromSeconds(1)),
            () => TryLockFile(Path.Combine(dataDir, SwapLockFileName)),
            () => RequestShutdown(port, TimeSpan.FromSeconds(2)),
            () => IsFree(Path.Combine(dataDir, "server.lock")),
            () => ServerAutoStart.StartDetached(ServerAutoStart.DefaultServerExe()),
            ServerAutoStart.DefaultMaxWait,
            TimeSpan.FromMilliseconds(100));
    }

    /// <summary>The stamp build-cli-and-server.mjs writes into bin-cli/build-stamp.txt; null when there is none.</summary>
    public static string? ReadStamp(string cliDirectory)
    {
        try
        {
            string file = Path.Combine(cliDirectory, "build-stamp.txt");
            if (!File.Exists(file)) return null;
            string stamp = File.ReadAllText(file).Trim();
            return stamp.Length > 0 ? stamp : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static Running Probe(int port, TimeSpan timeout)
    {
        try
        {
            using HttpClient client = new() { Timeout = timeout };
            using HttpResponseMessage response = client.GetAsync($"http://127.0.0.1:{port}/health").GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode) return new Running(false, null);
            string body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            try
            {
                using JsonDocument doc = JsonDocument.Parse(body);
                string? stamp = doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("buildStamp", out JsonElement value)
                    && value.ValueKind == JsonValueKind.String
                        ? value.GetString()
                        : null;
                return new Running(true, stamp);
            }
            catch (JsonException)
            {
                return new Running(true, null); // answers, build unknown: reused
            }
        }
        catch
        {
            return new Running(false, null);
        }
    }

    /// <summary>POST /shutdown, behind the same Host and Origin guard as every route.</summary>
    public static bool RequestShutdown(int port, TimeSpan timeout)
    {
        try
        {
            using HttpClient client = new() { Timeout = timeout };
            using HttpRequestMessage request = new(HttpMethod.Post, $"http://127.0.0.1:{port}/shutdown");
            using HttpResponseMessage response = client.Send(request);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>An exclusive handle on <paramref name="path"/>, or null when another process holds it. The same
    /// open as the server's single-instance lock (ProcessOwner.TryAcquireSingleInstanceLock).</summary>
    public static FileStream? TryLockFile(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>True when no server holds its single-instance lock: the old server has exited.</summary>
    public static bool IsFree(string serverLockPath)
    {
        using FileStream? probe = TryLockFile(serverLockPath);
        return probe is not null;
    }

    private static bool WaitFor(Func<bool> condition, TimeSpan maxWait, TimeSpan pollInterval)
    {
        Stopwatch clock = Stopwatch.StartNew();
        while (true)
        {
            if (condition()) return true;
            if (clock.Elapsed >= maxWait) return false;
            Thread.Sleep(pollInterval);
        }
    }
}
