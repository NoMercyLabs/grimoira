using System.Diagnostics;
using Grimoira.Server.Data;

namespace Grimoira.Cli.Tools;

/// <summary>
/// The one retry loop every client of the service shares (ThinClient, HookForwarder, McpBridge). The service
/// exits by itself when idle, so a client can meet it half gone: the listener is closed but the process still
/// holds server.lock. A refused connection is followed by a wait of <see cref="Spacing"/>, then (with a
/// <see cref="LostCallGuard"/>) a wait until that lock is free, then <c>ensureServer</c> (which starts a service
/// when none answers /health) and a resend, up to <see cref="MaxResends"/> times.
///
/// Any other failure may mean the call already ran. It is resent only when the guard proves it never started: a
/// stopping service closes a connection whose request it never read (on Windows the client may be connected to a
/// pipe instance the service never read from), and the service's <see cref="CleanExitRecord"/> lists every call id
/// it started. An attempt returns once the response headers are in, so a failure inside the loop means no answer
/// arrived. A call that may have run is reported (<see cref="LostCallException"/>), never repeated.
/// </summary>
public static class RefusedConnectionRetry
{
    public const int MaxResends = 3;
    public static readonly TimeSpan Spacing = TimeSpan.FromMilliseconds(200);

    /// <summary>The longest one wait for the old service to free server.lock may take.</summary>
    public static readonly TimeSpan ExitWait = ServerAutoStart.DefaultMaxWait;

    /// <summary>All waits of one call together, for a caller without a deadline of its own (ThinClient, McpBridge).</summary>
    public static readonly TimeSpan DefaultWaitBudget = TimeSpan.FromSeconds(30);

    /// <summary>The header that carries a call's id; the same id on every resend of one logical call.</summary>
    public const string CallIdHeader = "X-Grimoira-Call";

    /// <summary>True with the answer, or false when the service stayed unreachable after every resend. Any other
    /// failure propagates, unless <paramref name="guard"/> proves the call never ran.</summary>
    public static bool TrySend<T>(Func<T> attempt, Func<bool> ensureServer, out T? result, LostCallGuard? guard = null)
    {
        for (int resend = 0; ; resend++)
        {
            DateTime sentUtc = DateTime.UtcNow;
            try
            {
                result = attempt();
                return true;
            }
            catch (HttpRequestException ex)
            {
                ThrowUnlessResendable(ex, sentUtc, guard);
                if (resend == MaxResends)
                {
                    result = default;
                    return false;
                }
                // Wait first: a service that is exiting frees server.lock within that time, so the start below
                // does not meet it. A service that cannot be started ends the loop.
                Thread.Sleep(Spacing);
                guard?.WaitForPriorService();
                if (guard is { Spent: true } || !ensureServer())
                {
                    result = default;
                    return false;
                }
            }
        }
    }

    /// <summary>The same loop for an async call: (reached, answer).</summary>
    public static async Task<(bool Reached, T? Result)> TrySendAsync<T>(Func<Task<T>> attempt, Func<bool> ensureServer, CancellationToken cancellationToken,
        LostCallGuard? guard = null)
    {
        for (int resend = 0; ; resend++)
        {
            DateTime sentUtc = DateTime.UtcNow;
            try
            {
                return (true, await attempt());
            }
            catch (HttpRequestException ex)
            {
                ThrowUnlessResendable(ex, sentUtc, guard);
                if (resend == MaxResends) return (false, default);
                await Task.Delay(Spacing, cancellationToken);
                guard?.WaitForPriorService();
                if (guard is { Spent: true } || !ensureServer()) return (false, default);
            }
        }
    }

    /// <summary>Returns when the call may be resent: a refused connection, or a failure the guard proves never ran.
    /// A call the exited service cannot prove it did not run becomes a <see cref="LostCallException"/>; any other
    /// failure propagates as it is. (Not an exception filter: an exception thrown inside a filter is swallowed.)</summary>
    private static void ThrowUnlessResendable(HttpRequestException ex, DateTime sentUtc, LostCallGuard? guard)
    {
        if (ex.HttpRequestError == HttpRequestError.ConnectionError) return;
        LostCallVerdict verdict = guard?.Judge(sentUtc) ?? LostCallVerdict.Unknown;
        if (verdict == LostCallVerdict.NeverRan) return;
        if (verdict == LostCallVerdict.MayHaveRun) throw new LostCallException(ex);
        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(ex);
    }
}

/// <summary>The service exited while the call was in flight and cannot prove it did not run it.</summary>
public sealed class LostCallException(Exception inner)
    : HttpRequestException("the service exited while the call was in flight; it may have run; not repeated", inner);

/// <summary>
/// One logical call's id (sent in <see cref="RefusedConnectionRetry.CallIdHeader"/> on every resend) and its budget
/// for all waits together, so a call never hangs on waits for longer than <paramref name="waitBudget"/>.
/// </summary>
public sealed class LostCallGuard(string dataDir, TimeSpan waitBudget)
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly string _lockPath = Path.Combine(dataDir, "server.lock");

    public string CallId { get; } = Guid.NewGuid().ToString("N");

    private TimeSpan Remaining => waitBudget - _clock.Elapsed;

    /// <summary>True once the call's budget for waits and starts is used up: no further start or resend.</summary>
    public bool Spent => Remaining <= TimeSpan.Zero;

    /// <summary>The refused path: waits (at most <see cref="RefusedConnectionRetry.ExitWait"/> and the budget) until
    /// the old service has freed server.lock, or a service answers /health again (another client started one).</summary>
    public void WaitForPriorService() =>
        WaitUntil(() => ServerHandover.IsFree(_lockPath) || ServerAutoStart.IsHealthy(dataDir, TimeSpan.FromMilliseconds(250)));

    /// <summary>A call that failed with no answer: waits until the service freed server.lock, then judges the call by
    /// the service's <see cref="CleanExitRecord"/>. <see cref="LostCallVerdict.Unknown"/> when the lock stays held.</summary>
    public LostCallVerdict Judge(DateTime sentUtc) =>
        WaitUntil(() => ServerHandover.IsFree(_lockPath))
            ? CleanExitRecord.Judge(CleanExitRecord.Read(dataDir), sentUtc, CallId)
            : LostCallVerdict.Unknown;

    private bool WaitUntil(Func<bool> condition)
    {
        Stopwatch wait = Stopwatch.StartNew();
        while (!condition())
        {
            if (wait.Elapsed >= RefusedConnectionRetry.ExitWait || Remaining <= TimeSpan.Zero) return false;
            Thread.Sleep(50);
        }
        return true;
    }
}
