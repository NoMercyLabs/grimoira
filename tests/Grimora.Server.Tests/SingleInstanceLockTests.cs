using Grimora.Server.Data;
using Xunit;

namespace Grimora.Server.Tests;

// RESTRUCTURE.md "Slice 25": "Single instance: reuse ... ProcessOwner.cs (slice 23a); a second copy
// exits non-zero with a clear message." This is the lock primitive Program.cs uses at startup; a
// process-level "second copy exits non-zero" proof is the manual run in the slice report, since
// exercising that path in-process would call Environment.Exit and tear down the test host.
public class SingleInstanceLockTests
{
    [Fact]
    public void SecondAcquireOnTheSameDataDirIsRefused()
    {
        string dataDir = Directory.CreateTempSubdirectory("grimora-single-instance-").FullName;
        try
        {
            using FileStream? first = ProcessOwner.TryAcquireSingleInstanceLock(dataDir);
            Assert.NotNull(first);

            using FileStream? second = ProcessOwner.TryAcquireSingleInstanceLock(dataDir);
            Assert.Null(second);
        }
        finally
        {
            Directory.Delete(dataDir, recursive: true);
        }
    }

    [Fact]
    public void LockIsAvailableAgainAfterReleased()
    {
        string dataDir = Directory.CreateTempSubdirectory("grimora-single-instance-").FullName;
        try
        {
            FileStream? first = ProcessOwner.TryAcquireSingleInstanceLock(dataDir);
            Assert.NotNull(first);
            first.Dispose();

            using FileStream? second = ProcessOwner.TryAcquireSingleInstanceLock(dataDir);
            Assert.NotNull(second);
        }
        finally
        {
            Directory.Delete(dataDir, recursive: true);
        }
    }

    // The service holds server.lock until its exit record is written. The lock handle is referred to by nothing after
    // startup, so without an explicit hold a garbage collection (the stop itself runs one) closes it: the lock is free
    // while the service still stops, a second service can start on the data directory, and a client reads a missing
    // exit record as "the call may have run". A tiny first GC generation makes the service collect often.
    [Fact]
    public async Task TheStoppingServiceHoldsItsLockUntilItsExitRecordIsWritten()
    {
        string dataDir = Directory.CreateTempSubdirectory("grimora-single-instance-").FullName;
        try
        {
            using RunningServer server = RunningServer.Start(dataDir, new Dictionary<string, string> { ["DOTNET_GCgen0size"] = "10000" });
            using (HttpClient client = server.CreateClient())
                Assert.True((await client.PostAsync("/shutdown", null)).IsSuccessStatusCode);
            System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
            FileStream? freed = null;
            while (freed is null && clock.Elapsed < TimeSpan.FromSeconds(20))
            {
                freed = ProcessOwner.TryAcquireSingleInstanceLock(dataDir);
                if (freed is null) await Task.Delay(5);
            }
            using (freed)
            {
                Assert.NotNull(freed);
                Assert.True(File.Exists(Path.Combine(dataDir, CleanExitRecord.FileName)),
                    $"server.lock was free after {clock.ElapsedMilliseconds} ms of the stop, before the service wrote its exit record");
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(dataDir, recursive: true); } catch (IOException) { /* the service may still be closing its store */ }
        }
    }
}
