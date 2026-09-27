using Aitm.Server.Data;
using Xunit;

namespace Aitm.Server.Tests;

// RESTRUCTURE.md "Slice 25": "Single instance: reuse ... ProcessOwner.cs (slice 23a); a second copy
// exits non-zero with a clear message." This is the lock primitive Program.cs uses at startup; a
// process-level "second copy exits non-zero" proof is the manual run in the slice report, since
// exercising that path in-process would call Environment.Exit and tear down the test host.
public class SingleInstanceLockTests
{
    [Fact]
    public void SecondAcquireOnTheSameDataDirIsRefused()
    {
        string dataDir = Directory.CreateTempSubdirectory("aitm-single-instance-").FullName;
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
        string dataDir = Directory.CreateTempSubdirectory("aitm-single-instance-").FullName;
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
}
