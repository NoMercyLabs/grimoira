namespace Grimoira.Cli.Tools;

/// <summary>
/// `grimoira hook SessionStart` (RESTRUCTURE.md slice 28): "A SessionStart check starts the server when
/// /health does not answer." The start-and-wait itself is <see cref="ServerAutoStart"/>, shared with the
/// thin client (slice 29d). Every failure is quiet and the hook exits 0, so a session is never blocked; the
/// MCP entry then fails on its own and a /mcp reconnect retries.
/// </summary>
public static class SessionStartServerCheck
{
    /// <summary>Under the 15 s SessionStart timeout in hooks.json.</summary>
    public static readonly TimeSpan DefaultMaxWait = ServerAutoStart.DefaultMaxWait;

    public static int Run(Func<bool> isHealthy, Func<bool> tryStart, TimeSpan maxWait, TimeSpan pollInterval)
    {
        ServerAutoStart.EnsureRunning(isHealthy, tryStart, maxWait, pollInterval);
        return 0;
    }

    /// <summary>The real hook: the port and server path of <see cref="ServerAutoStart.EnsureRunning()"/>, and a
    /// running server of another build hands over to this CLI's build (<see cref="ServerHandover"/>, slice 32b).</summary>
    public static int RunDefault()
    {
        ServerHandover.RunDefault();
        return 0;
    }

    public static string DefaultServerPath(string cliDirectory) => ServerAutoStart.DefaultServerPath(cliDirectory);

    public static bool IsHealthy(string dataDir, TimeSpan timeout) => ServerAutoStart.IsHealthy(dataDir, timeout);
}
