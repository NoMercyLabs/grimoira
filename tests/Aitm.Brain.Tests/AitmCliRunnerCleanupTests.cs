using Aitm.TestSupport;
using Xunit;

namespace Aitm.Brain.Tests;

/// <summary>
/// Every Brain test's `finally` block calls <see cref="AitmCliRunner.DeleteInstance"/> to clean up its
/// throwaway store. On Windows, WAL-mode sqlite (StoreConnection.ApplyPragmas turns it on) can leave a
/// transient handle on the `-wal`/`-shm` file past <c>SqliteConnection.ClearAllPools()</c>, so an unguarded
/// <c>Directory.Delete</c> right after it intermittently throws — turning an otherwise-passing test into a
/// reported failure with no useful message, and leaking the instance directory under `~/.aitm` for good
/// (confirmed independently: several `test-*` directories sit there today with nothing left to clean them
/// up). These tests pin the fix: DeleteInstance must retry past a transient lock instead of surfacing it,
/// and a periodic sweep must clear out only what nothing owns any more.
/// </summary>
public class AitmCliRunnerCleanupTests
{
    [Fact]
    public void DeleteInstanceRetriesUntilATransientWindowsFileLockClears()
    {
        string instance = AitmCliRunner.NewTestInstance("delete-retry");
        string dir = AitmCliRunner.InstanceDir(instance);
        Directory.CreateDirectory(dir);
        string walFile = Path.Combine(dir, "aitm.db-wal");
        File.WriteAllText(walFile, "x");

        // Simulates the native sqlite handle that Windows can hold on a WAL file for a few hundred
        // milliseconds after SqliteConnection.ClearAllPools() returns: opened without FileShare.Delete, so
        // a Directory.Delete attempted while this is open must fail, exactly like the real race.
        FileStream lockHandle = new(walFile, FileMode.Open, FileAccess.Read, FileShare.Read);
        _ = Task.Run(() =>
        {
            Thread.Sleep(400);
            lockHandle.Dispose();
        });

        // Must not throw: DeleteInstance has to retry past the lock instead of surfacing it to the caller.
        AitmCliRunner.DeleteInstance(instance);

        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public void SweepStaleInstancesRemovesOnlyInstancesOlderThanOneHour()
    {
        string staleInstance = AitmCliRunner.NewTestInstance("sweep-stale");
        string staleDir = AitmCliRunner.InstanceDir(staleInstance);
        Directory.CreateDirectory(staleDir);
        File.WriteAllText(Path.Combine(staleDir, "marker.txt"), "x");
        Directory.SetLastWriteTimeUtc(staleDir, DateTime.UtcNow.AddHours(-2));

        string freshInstance = AitmCliRunner.NewTestInstance("sweep-fresh");
        string freshDir = AitmCliRunner.InstanceDir(freshInstance);
        Directory.CreateDirectory(freshDir);
        File.WriteAllText(Path.Combine(freshDir, "marker.txt"), "x");

        try
        {
            AitmCliRunner.SweepStaleInstances();

            Assert.False(Directory.Exists(staleDir), "a test-* store untouched for over an hour should be swept");
            Assert.True(Directory.Exists(freshDir), "a store touched within the last hour must survive — another agent's run may still own it");
        }
        finally
        {
            if (Directory.Exists(staleDir)) Directory.Delete(staleDir, recursive: true);
            if (Directory.Exists(freshDir)) Directory.Delete(freshDir, recursive: true);
        }
    }
}
