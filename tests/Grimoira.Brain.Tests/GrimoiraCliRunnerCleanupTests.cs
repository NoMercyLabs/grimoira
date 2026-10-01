using Grimoira.TestSupport;
using Xunit;

namespace Grimoira.Brain.Tests;

/// <summary>
/// Every Brain test's `finally` block calls <see cref="GrimoiraCliRunner.DeleteInstance"/> to clean up its
/// throwaway store. On Windows, WAL-mode sqlite (StoreConnection.ApplyPragmas turns it on) can leave a
/// transient handle on the `-wal`/`-shm` file past <c>SqliteConnection.ClearAllPools()</c>, so an unguarded
/// <c>Directory.Delete</c> right after it intermittently throws — turning an otherwise-passing test into a
/// reported failure with no useful message, and leaking the instance directory under `~/.grimoira` for good
/// (confirmed independently: several `test-*` directories sit there today with nothing left to clean them
/// up). These tests pin the fix: DeleteInstance must retry past a transient lock instead of surfacing it,
/// and a test must leave no store of its own behind.
/// </summary>
public class GrimoiraCliRunnerCleanupTests
{
    [Fact]
    public void DeleteInstanceRetriesUntilATransientWindowsFileLockClears()
    {
        string instance = GrimoiraCliRunner.NewTestInstance("delete-retry");
        string dir = GrimoiraCliRunner.InstanceDir(instance);
        Directory.CreateDirectory(dir);
        string walFile = Path.Combine(dir, "grimoira.db-wal");
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
        GrimoiraCliRunner.DeleteInstance(instance);

        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public void ATestStoreDoesNotSurviveCleanup()
    {
        string instance = GrimoiraCliRunner.NewTestInstance("cleanup-proof");
        string dir = GrimoiraCliRunner.InstanceDir(instance);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "marker.txt"), "x");
        GrimoiraCliRunner.DeleteInstance(instance);
        Assert.False(Directory.Exists(dir));
    }
}
