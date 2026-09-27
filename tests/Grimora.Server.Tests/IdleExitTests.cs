using Grimora.Server.Data;
using Xunit;

namespace Grimora.Server.Tests;

// The service exits by itself after a quiet spell (RESTRUCTURE.md "Phase 4, replaced": on demand, nobody is
// any wiser). Real work resets the timer; a call still in flight keeps the service alive.
public sealed class IdleExitTests
{
    private static readonly TimeSpan Idle = TimeSpan.FromMinutes(30);
    private DateTime _now = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
    private int _stops;

    private IdleExit Create() => new(Idle, () => _stops++, () => _now);

    [Fact]
    public void ANoCallSpellOfTheIdleTimeStopsTheService()
    {
        IdleExit exit = Create();
        _now += Idle;

        Assert.True(exit.CheckAndStopIfIdle());
        Assert.Equal(1, _stops);
    }

    [Fact]
    public void ALessThanIdleSpellKeepsTheServiceRunning()
    {
        IdleExit exit = Create();
        _now += Idle - TimeSpan.FromSeconds(1);

        Assert.False(exit.CheckAndStopIfIdle());
        Assert.Equal(0, _stops);
    }

    [Fact]
    public void ACallResetsTheTimer()
    {
        IdleExit exit = Create();
        _now += Idle - TimeSpan.FromSeconds(1);
        exit.Begin().Dispose();
        _now += Idle - TimeSpan.FromSeconds(1);

        Assert.False(exit.CheckAndStopIfIdle());
        _now += TimeSpan.FromSeconds(1);
        Assert.True(exit.CheckAndStopIfIdle());
    }

    [Fact]
    public void ACallInFlightBlocksTheExitHoweverLongItRuns()
    {
        IdleExit exit = Create();
        IDisposable call = exit.Begin();
        _now += Idle * 3;

        Assert.False(exit.CheckAndStopIfIdle());

        call.Dispose();
        Assert.False(exit.CheckAndStopIfIdle()); // the end of a call is activity too
        _now += Idle;
        Assert.True(exit.CheckAndStopIfIdle());
    }
}
