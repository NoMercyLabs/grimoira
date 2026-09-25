using System.Diagnostics;

namespace Aitm.Cli.Tools;

/// <summary>
/// `aitm hook SessionStart` (RESTRUCTURE.md slice 28): "A SessionStart check starts the server when
/// /health does not answer." Server up: reused, nothing started. Server down: the published server is
/// started detached, then /health is polled for a bounded time. Every failure is quiet and the hook exits
/// 0, so a session is never blocked; the MCP entry then fails on its own and a /mcp reconnect retries.
/// </summary>
public static class SessionStartServerCheck
{
    /// <summary>Under the 15 s SessionStart timeout in hooks.json.</summary>
    public static readonly TimeSpan DefaultMaxWait = TimeSpan.FromSeconds(10);

    public static int Run(Func<bool> isHealthy, Func<bool> tryStart, TimeSpan maxWait, TimeSpan pollInterval)
    {
        try
        {
            if (isHealthy()) return 0;
            if (!tryStart()) return 0;

            Stopwatch sw = Stopwatch.StartNew();
            while (sw.Elapsed < maxWait)
            {
                Thread.Sleep(pollInterval);
                if (isHealthy()) return 0;
            }
        }
        catch
        {
            // Hooks must never block or crash a session.
        }
        return 0;
    }

    /// <summary>The real hook: the port Aitm.Server uses (<c>AITM_SERVER_PORT</c>, else 7635) and the
    /// server at <c>AITM_SERVER_EXE</c>, else <see cref="DefaultServerPath"/> beside this CLI.</summary>
    public static int RunDefault()
    {
        int port = int.TryParse(Environment.GetEnvironmentVariable("AITM_SERVER_PORT"), out int configured) ? configured : 7635;
        string serverPath = Environment.GetEnvironmentVariable("AITM_SERVER_EXE") is { Length: > 0 } exe
            ? exe
            : DefaultServerPath(AppContext.BaseDirectory);
        return Run(
            () => IsHealthy(port, TimeSpan.FromSeconds(1)),
            () => StartDetached(serverPath),
            DefaultMaxWait,
            TimeSpan.FromMilliseconds(250));
    }

    /// <summary>
    /// The published server: <c>&lt;plugin root&gt;/bin-server/Aitm.Server(.exe)</c>, the sibling of the
    /// CLI's <c>bin-cli</c>. Slice 29 publishes it; until then this path does not exist and the check
    /// starts nothing.
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

    private static bool StartDetached(string serverPath)
    {
        if (!File.Exists(serverPath)) return false;
        ProcessStartInfo psi = new(serverPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(serverPath) ?? "",
        };
        // The server serves every project through headers; it must not pin itself to this session's one.
        psi.Environment.Remove("CLAUDE_PROJECT_DIR");
        psi.Environment.Remove("AITM_INSTANCE");
        using Process? process = Process.Start(psi);
        return process is not null;
    }
}
