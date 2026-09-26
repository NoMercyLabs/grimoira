using System.Diagnostics;

namespace Aitm.Cli.Tools;

/// <summary>
/// The one start-and-wait path for Aitm.Server, shared by `aitm hook SessionStart` (slice 28) and the thin
/// client (slice 29d). Server up: reused, nothing started. Server down: the published server is started
/// detached, then /health is polled for a bounded time. Never throws; the caller decides what a server that
/// still does not answer means (the hook stays quiet, the thin client prints one line and exits 1).
/// </summary>
public static class ServerAutoStart
{
    /// <summary>Under the 15 s SessionStart timeout in hooks.json.</summary>
    public static readonly TimeSpan DefaultMaxWait = TimeSpan.FromSeconds(10);

    /// <summary>True when /health answers, before or after a start.</summary>
    public static bool EnsureRunning(Func<bool> isHealthy, Func<bool> tryStart, TimeSpan maxWait, TimeSpan pollInterval)
    {
        try
        {
            if (isHealthy()) return true;
            if (!tryStart()) return false;

            Stopwatch sw = Stopwatch.StartNew();
            while (sw.Elapsed < maxWait)
            {
                Thread.Sleep(pollInterval);
                if (isHealthy()) return true;
            }
        }
        catch
        {
            // A failed probe or start is the same as a server that does not answer.
        }
        return false;
    }

    /// <summary><see cref="EnsureRunning(Func{bool}, Func{bool}, TimeSpan, TimeSpan)"/> on the real port and
    /// server path: <see cref="DefaultPort"/> and <see cref="DefaultServerExe"/>.</summary>
    public static bool EnsureRunning() => EnsureRunning(
        () => IsHealthy(DefaultPort(), TimeSpan.FromSeconds(1)),
        () => StartDetached(DefaultServerExe()),
        DefaultMaxWait,
        TimeSpan.FromMilliseconds(250));

    /// <summary>The port Aitm.Server uses: <c>AITM_SERVER_PORT</c>, else 7635.</summary>
    public static int DefaultPort() =>
        int.TryParse(Environment.GetEnvironmentVariable("AITM_SERVER_PORT"), out int configured) ? configured : 7635;

    /// <summary>The server at <c>AITM_SERVER_EXE</c>, else <see cref="DefaultServerPath"/> beside this CLI.</summary>
    public static string DefaultServerExe() =>
        Environment.GetEnvironmentVariable("AITM_SERVER_EXE") is { Length: > 0 } exe
            ? exe
            : DefaultServerPath(AppContext.BaseDirectory);

    /// <summary>
    /// The published server: <c>&lt;plugin root&gt;/bin-server/Aitm.Server(.exe)</c>, the sibling of the
    /// CLI's <c>bin-cli</c>.
    /// </summary>
    public static string DefaultServerPath(string cliDirectory)
    {
        string name = OperatingSystem.IsWindows() ? "Aitm.Server.exe" : "Aitm.Server";
        return Path.GetFullPath(Path.Combine(cliDirectory.TrimEnd('/', '\\'), "..", "bin-server", name));
    }

    public static bool IsHealthy(int port, TimeSpan timeout)
    {
        try
        {
            using HttpClient client = new() { Timeout = timeout };
            using HttpResponseMessage response = client.GetAsync($"http://127.0.0.1:{port}/health").GetAwaiter().GetResult();
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public static bool StartDetached(string serverPath)
    {
        if (!File.Exists(serverPath)) return false;
        // The server must not hold this CLI's stdout/stderr pipes: whoever reads them (a script, a hook
        // runner, a test) would wait for end-of-stream until the server exits. On Windows CreateProcess
        // passes every inheritable handle, so only ShellExecute (no handle inheritance) avoids that; it takes
        // this process's environment, so the two project variables are cleared around the start. On Unix
        // .NET marks its own descriptors close-on-exec, so redirecting stdio and closing our ends is enough.
        bool windows = OperatingSystem.IsWindows();
        ProcessStartInfo psi = new(serverPath)
        {
            UseShellExecute = windows,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetDirectoryName(serverPath) ?? "",
            RedirectStandardInput = !windows,
            RedirectStandardOutput = !windows,
            RedirectStandardError = !windows,
        };
        // The server serves every project through headers; it must not pin itself to this session's one.
        string[] pinned = ["CLAUDE_PROJECT_DIR", "AITM_INSTANCE"];
        Dictionary<string, string?> saved = pinned.ToDictionary(n => n, Environment.GetEnvironmentVariable);
        try
        {
            if (windows) foreach (string name in pinned) Environment.SetEnvironmentVariable(name, null);
            else foreach (string name in pinned) psi.Environment.Remove(name);
            using Process? process = Process.Start(psi);
            if (process is null) return false;
            if (!windows)
            {
                process.StandardInput.Close();
                process.StandardOutput.Close();
                process.StandardError.Close();
            }
            return true;
        }
        finally
        {
            if (windows) foreach ((string name, string? value) in saved) Environment.SetEnvironmentVariable(name, value);
        }
    }
}
