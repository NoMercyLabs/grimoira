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

    /// <summary>The idle time when nothing configures one (also what `aitm service status` assumes of an older server).</summary>
    public static readonly TimeSpan DefaultIdle = TimeSpan.FromMinutes(ServerAddress.DefaultIdleMinutes);

    /// <summary>Calls started and not yet ended, including a /cli verb that outlived its timeout and still runs.</summary>
    public int InFlight
    {
        get { lock (_gate) return _inFlight; }
    }

    /// <summary>Waits until no call is in flight; false when <paramref name="timeout"/> passed first. The shutdown
    /// runs this before it disposes the stores, so a verb still running on its own thread keeps its store.</summary>
    public bool WaitForDrain(TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        lock (_gate)
        {
            while (_inFlight > 0)
            {
                TimeSpan left = deadline - DateTime.UtcNow;
                if (left <= TimeSpan.Zero) return false;
                Monitor.Wait(_gate, left);
            }
            return true;
        }
    }

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
            lock (owner._gate) { owner._inFlight--; owner._lastActivity = owner._now(); Monitor.PulseAll(owner._gate); }
        }
    }

    /// <summary>The idle time: <c>AITM_IDLE_SECONDS</c> (a test seam), else <c>AITM_IDLE_MINUTES</c>, else <see cref="DefaultIdle"/>.</summary>
    public static TimeSpan ConfiguredIdle()
    {
        if (int.TryParse(Environment.GetEnvironmentVariable("AITM_IDLE_SECONDS"), out int seconds) && seconds > 0)
            return TimeSpan.FromSeconds(seconds);
        if (int.TryParse(Environment.GetEnvironmentVariable("AITM_IDLE_MINUTES"), out int minutes) && minutes > 0)
            return TimeSpan.FromMinutes(minutes);
        return DefaultIdle;
    }
}
