using Grimora.Cli.Tools;
using Grimora.Server.Data;
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

    [Fact]
    public void TheRecordProvesACallNeverRanOnlyWhenItCoversTheSendAndLacksTheId()
    {
        DateTime sent = DateTime.UtcNow;
        CleanExitRecord record = new(sent.AddSeconds(-5), sent.AddSeconds(1), Clean: true, Wrapped: false, sent.AddSeconds(-5), ["other"]);

        Assert.Equal(LostCallVerdict.NeverRan, CleanExitRecord.Judge(record, sent, "mine"));
        Assert.Equal(LostCallVerdict.MayHaveRun, CleanExitRecord.Judge(record with { CallIds = ["mine"] }, sent, "mine"));
        Assert.Equal(LostCallVerdict.MayHaveRun, CleanExitRecord.Judge(record with { Clean = false }, sent, "mine"));
        Assert.Equal(LostCallVerdict.MayHaveRun, CleanExitRecord.Judge(record, sent.AddSeconds(-10), "mine"));
        Assert.Equal(LostCallVerdict.MayHaveRun, CleanExitRecord.Judge(null, sent, "mine"));
    }

    [Fact]
    public void AWrappedRingThatNoLongerReachesTheSendTimeProvesNothing()
    {
        DateTime sent = DateTime.UtcNow;
        CleanExitRecord wrapped = new(sent.AddSeconds(-60), sent.AddSeconds(1), Clean: true, Wrapped: true, OldestKeptStartUtc: sent.AddSeconds(-1), ["other"]);

        Assert.Equal(LostCallVerdict.MayHaveRun, CleanExitRecord.Judge(wrapped, sent.AddSeconds(-2), "mine"));
        Assert.Equal(LostCallVerdict.NeverRan, CleanExitRecord.Judge(wrapped, sent, "mine"));
    }

    [Fact]
    public void TheRecordSurvivesAWriteAndARead()
    {
        string dataDir = Directory.CreateTempSubdirectory("grimora-exit-record-").FullName;
        try
        {
            DateTime at = DateTime.UtcNow;
            new CleanExitRecord(at, at.AddSeconds(1), Clean: true, Wrapped: true, at, ["a", "b"]).Write(dataDir);
            CleanExitRecord? read = CleanExitRecord.Read(dataDir);
            Assert.NotNull(read);
            Assert.Equal((at, true, true), (read.StartedUtc, read.Clean, read.Wrapped));
            Assert.Equal(["a", "b"], read.CallIds);
        }
        finally
        {
            Directory.Delete(dataDir, recursive: true);
        }
    }

    [Fact]
    public void ARefusedCallWaitsForTheOldServiceToFreeItsLockBeforeStartingANewOne()
    {
        string dataDir = Directory.CreateTempSubdirectory("grimora-old-holder-").FullName;
        string lockPath = Path.Combine(dataDir, "server.lock");
        // The old service: its pipe already refuses, but it holds server.lock until the test lets go.
        FileStream oldHolder = new(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            using Timer release = new(_ => oldHolder.Dispose(), null, TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan);
            int attempts = 0;
            bool? lockFreeAtStart = null;

            bool reached = RefusedConnectionRetry.TrySend(() =>
            {
                if (++attempts == 1) throw Refused();
                return "answered";
            }, () =>
            {
                lockFreeAtStart = ServerHandover.IsFree(lockPath);
                return true;
            }, out string? answer, new LostCallGuard(dataDir, TimeSpan.FromSeconds(30)));

            Assert.True(reached);
            Assert.Equal("answered", answer);
            Assert.True(lockFreeAtStart, "a new service was started while the old one still held server.lock");
        }
        finally
        {
            oldHolder.Dispose();
            Directory.Delete(dataDir, recursive: true);
        }
    }
}
