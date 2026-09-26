using System.Net.Http;
using Aitm.Cli.Tools;
using Xunit;

namespace Aitm.Cli.Tests;

// A client that connects while the service is exiting (the idle exit) must end with its call answered by a new
// service, silently: a refused connection is retried up to 3 times, 200 ms apart, with the service started in
// between. Only a refused connection is resent; any other failure may mean the call already ran.
public sealed class RefusedConnectionRetryTests
{
    private static HttpRequestException Refused() => new(HttpRequestError.ConnectionError, "refused");

    [Fact]
    public void ACallRefusedWhileTheServiceExitsIsAnsweredOnTheThirdResend()
    {
        int attempts = 0;
        int starts = 0;

        bool reached = RefusedConnectionRetry.TrySend(() =>
        {
            if (++attempts <= 3) throw Refused();
            return "answered";
        }, () => { starts++; return true; }, out string? answer);

        Assert.True(reached);
        Assert.Equal("answered", answer);
        Assert.Equal(4, attempts);
        Assert.Equal(3, starts);
    }

    [Fact]
    public void AServiceThatStaysGoneIsGivenUpOnAfterThreeResends()
    {
        int attempts = 0;

        bool reached = RefusedConnectionRetry.TrySend<string>(() => { attempts++; throw Refused(); }, () => true, out string? answer);

        Assert.False(reached);
        Assert.Null(answer);
        Assert.Equal(4, attempts);
    }

    [Fact]
    public void ResendsAreSpacedAtLeast200Ms()
    {
        System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        int attempts = 0;

        RefusedConnectionRetry.TrySend(() => { if (++attempts <= 2) throw Refused(); return 1; }, () => true, out int _);

        Assert.True(clock.ElapsedMilliseconds >= 380, $"two resends took {clock.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void AFailureThatIsNotARefusedConnectionIsNeverResent()
    {
        int attempts = 0;

        Assert.Throws<TaskCanceledException>(() => RefusedConnectionRetry.TrySend<string>(
            () => { attempts++; throw new TaskCanceledException("timed out mid-call"); }, () => true, out _));

        Assert.Equal(1, attempts);
    }
}
