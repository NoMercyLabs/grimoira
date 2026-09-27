using Grimora.Server.Data;

namespace Grimora.Cli.Tools;

/// <summary>
/// The one retry loop every client of the service shares (ThinClient, HookForwarder, McpBridge). The service
/// exits by itself when idle, so a client can meet it half gone: the listener is closed but the process still
/// holds server.lock. A refused connection is followed by a wait and <c>ensureServer</c> (which starts a service when
/// none answers /health; a new service that meets the old one's lock exits at once, cleanly) and a resend, up to
/// <see cref="MaxResends"/> times, <see cref="Spacing"/> apart. A refused connection is resent. Any other failure
/// may mean the call already ran, so it is resent only with proof that it did not: a stopping service closes a
/// connection whose request it never read (on Windows the client may be connected to a pipe instance the service
/// never read from), and a service that answered every call it started leaves <see cref="CleanExitRecord"/>. The
/// call is resent when that record covers the moment it was sent (<see cref="LostCallNeverRan"/>); never otherwise.
/// </summary>
public static class RefusedConnectionRetry
{
    public const int MaxResends = 3;
    public static readonly TimeSpan Spacing = TimeSpan.FromMilliseconds(200);

    /// <summary>How long <see cref="LostCallNeverRan"/> waits for the service to free server.lock.</summary>
    public static readonly TimeSpan ExitWait = ServerAutoStart.DefaultMaxWait;

    /// <summary>For a call sent at the given time that failed with no answer: waits until the service for
    /// <paramref name="dataDir"/> has freed server.lock, then true when its <see cref="CleanExitRecord"/> covers that
    /// time, so the call never ran. False when the lock stays held (the service still runs, or another took over).</summary>
    public static Func<DateTime, bool> LostCallNeverRan(string dataDir) => sentUtc =>
    {
        string lockPath = Path.Combine(dataDir, "server.lock");
        System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        while (!ServerHandover.IsFree(lockPath))
        {
            if (clock.Elapsed >= ExitWait) return false;
            Thread.Sleep(50);
        }
        return CleanExitRecord.Covers(dataDir, sentUtc);
    };

    private static bool Resendable(HttpRequestException ex, DateTime sentUtc, Func<DateTime, bool>? lostCallNeverRan) =>
        ex.HttpRequestError == HttpRequestError.ConnectionError || (lostCallNeverRan is not null && lostCallNeverRan(sentUtc));

    /// <summary>True with the answer, or false when the service stayed unreachable after every resend. Any other
    /// failure propagates, unless <paramref name="lostCallNeverRan"/> proves the call never ran.</summary>
    public static bool TrySend<T>(Func<T> attempt, Func<bool> ensureServer, out T? result, Func<DateTime, bool>? lostCallNeverRan = null)
    {
        for (int resend = 0; ; resend++)
        {
            DateTime sentUtc = DateTime.UtcNow;
            try
            {
                result = attempt();
                return true;
            }
            catch (HttpRequestException ex) when (Resendable(ex, sentUtc, lostCallNeverRan))
            {
                if (resend == MaxResends)
                {
                    result = default;
                    return false;
                }
                // Wait first: a service that is exiting frees server.lock within that time, so the start below
                // does not meet it. A service that cannot be started (none installed, or it never answers)
                // ends the loop; retrying it would only repeat the wait.
                Thread.Sleep(Spacing);
                if (!ensureServer())
                {
                    result = default;
                    return false;
                }
            }
        }
    }

    /// <summary>The same loop for an async call: (reached, answer).</summary>
    public static async Task<(bool Reached, T? Result)> TrySendAsync<T>(Func<Task<T>> attempt, Func<bool> ensureServer, CancellationToken cancellationToken,
        Func<DateTime, bool>? lostCallNeverRan = null)
    {
        for (int resend = 0; ; resend++)
        {
            DateTime sentUtc = DateTime.UtcNow;
            try
            {
                return (true, await attempt());
            }
            catch (HttpRequestException ex) when (Resendable(ex, sentUtc, lostCallNeverRan))
            {
                if (resend == MaxResends) return (false, default);
                await Task.Delay(Spacing, cancellationToken);
                if (!ensureServer()) return (false, default);
            }
        }
    }
}
