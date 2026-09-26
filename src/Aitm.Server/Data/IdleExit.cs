namespace Aitm.Server.Data;

/// <summary>
/// The on-demand service exits by itself after a quiet spell (RESTRUCTURE.md "Phase 4, replaced"). Real work
/// (a call that starts or ends) resets the timer; a call in flight blocks the exit however long it runs.
/// /health is not real work and never reaches this class, so a client probing the service cannot keep it alive.
/// </summary>
public sealed class IdleExit(TimeSpan idle, Action stop, Func<DateTime>? now = null)
{
    private readonly Func<DateTime> _now = now ?? (() => DateTime.UtcNow);
    private readonly object _gate = new();
    private int _inFlight;
    private DateTime _lastActivity = (now ?? (() => DateTime.UtcNow))();

    /// <summary>Marks a call as started; dispose it when the call ends.</summary>
    public IDisposable Begin()
    {
        lock (_gate) { _inFlight++; _lastActivity = _now(); }
        return new Call(this);
    }

    /// <summary>True (and stops the service) when nothing is in flight and the idle time has passed.</summary>
    public bool CheckAndStopIfIdle()
    {
        lock (_gate)
        {
            if (_inFlight > 0 || _now() - _lastActivity < idle) return false;
        }
        stop();
        return true;
    }

    private sealed class Call(IdleExit owner) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) != 0) return;
            lock (owner._gate) { owner._inFlight--; owner._lastActivity = owner._now(); }
        }
    }

    /// <summary>The idle time: <c>AITM_IDLE_SECONDS</c> (a test seam), else <c>AITM_IDLE_MINUTES</c>, else 30 minutes.</summary>
    public static TimeSpan ConfiguredIdle()
    {
        if (int.TryParse(Environment.GetEnvironmentVariable("AITM_IDLE_SECONDS"), out int seconds) && seconds > 0)
            return TimeSpan.FromSeconds(seconds);
        if (int.TryParse(Environment.GetEnvironmentVariable("AITM_IDLE_MINUTES"), out int minutes) && minutes > 0)
            return TimeSpan.FromMinutes(minutes);
        return TimeSpan.FromMinutes(30);
    }
}
