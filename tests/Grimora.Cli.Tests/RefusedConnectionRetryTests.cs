using Grimora.Cli.Tools;
using Xunit;

namespace Grimora.Cli.Tests;

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

        bool reached = RefusedConnectionRetry.TrySend(() => { attempts++; throw Refused(); }, () => true, out string? answer);

        Assert.False(reached);
        Assert.Null(answer);
        Assert.Equal(4, attempts);
    }

    [Fact]
    public void AServiceThatCannotBeStartedEndsTheLoopAtOnce()
    {
        int attempts = 0;

        bool reached = RefusedConnectionRetry.TrySend<string>(() => { attempts++; throw Refused(); }, () => false, out _);

        Assert.False(reached);
        Assert.Equal(1, attempts);
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

    private static HttpRequestException Dropped() => new("An error occurred while sending the request.", new IOException("Pipe is broken."));

    [Fact]
    public void ACallDroppedByAServiceThatProvedItNeverRanIsResent()
    {
        int attempts = 0;
        DateTime? askedAbout = null;
        DateTime before = DateTime.UtcNow;

        bool reached = RefusedConnectionRetry.TrySend(() =>
        {
            if (++attempts == 1) throw Dropped();
            return "answered";
        }, () => true, out string? answer, sent => { askedAbout = sent; return true; });

        Assert.True(reached);
        Assert.Equal("answered", answer);
        Assert.Equal(2, attempts);
        Assert.True(askedAbout >= before, "the proof was asked for a time before the call was sent");
    }

    [Fact]
    public void ACallDroppedWithoutProofThatItNeverRanIsNotResent()
    {
        int attempts = 0;

        Assert.Throws<HttpRequestException>(() => RefusedConnectionRetry.TrySend<string>(
            () => { attempts++; throw Dropped(); }, () => true, out _, _ => false));

        Assert.Equal(1, attempts);
    }

    [Fact]
    public void TheProofNeedsAFreeLockAndACleanExitRecordCoveringTheCall()
    {
        string dataDir = Directory.CreateTempSubdirectory("grimora-lost-call-").FullName;
        try
        {
            DateTime sent = DateTime.UtcNow;
            Func<DateTime, bool> neverRan = RefusedConnectionRetry.LostCallNeverRan(dataDir);
            Assert.False(neverRan(sent));

            Grimora.Server.Data.CleanExitRecord.Write(dataDir, sent.AddSeconds(-5), sent.AddSeconds(1));
            Assert.True(neverRan(sent));
            // A service that started after the call was sent is not the one the call went to.
            Assert.False(neverRan(sent.AddSeconds(-10)));

            using FileStream held = new(Path.Combine(dataDir, "server.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
            Assert.False(neverRan(sent));
            Assert.True(clock.Elapsed >= RefusedConnectionRetry.ExitWait, "a held lock must be waited on before the proof is refused");
        }
        finally
        {
            Directory.Delete(dataDir, recursive: true);
        }
    }
}
