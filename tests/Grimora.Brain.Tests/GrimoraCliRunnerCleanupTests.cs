using Grimora.TestSupport;
using Xunit;

namespace Grimora.Brain.Tests;

/// <summary>
/// Every Brain test's `finally` block calls <see cref="GrimoraCliRunner.DeleteInstance"/> to clean up its
/// throwaway store. On Windows, WAL-mode sqlite (StoreConnection.ApplyPragmas turns it on) can leave a
/// transient handle on the `-wal`/`-shm` file past <c>SqliteConnection.ClearAllPools()</c>, so an unguarded
/// <c>Directory.Delete</c> right after it intermittently throws — turning an otherwise-passing test into a
/// reported failure with no useful message, and leaking the instance directory under `~/.grimora` for good
/// (confirmed independently: several `test-*` directories sit there today with nothing left to clean them
/// up). These tests pin the fix: DeleteInstance must retry past a transient lock instead of surfacing it,
/// and a test must leave no store of its own behind.
/// </summary>
public class GrimoraCliRunnerCleanupTests
{
    [Fact]
    public void DeleteInstanceRetriesUntilATransientWindowsFileLockClears()
    {
        string instance = GrimoraCliRunner.NewTestInstance("delete-retry");
        string dir = GrimoraCliRunner.InstanceDir(instance);
        Directory.CreateDirectory(dir);
        string walFile = Path.Combine(dir, "grimora.db-wal");
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
        GrimoraCliRunner.DeleteInstance(instance);

        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public void ATestStoreDoesNotSurviveCleanup()
    {
        string instance = GrimoraCliRunner.NewTestInstance("cleanup-proof");
        string dir = GrimoraCliRunner.InstanceDir(instance);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "marker.txt"), "x");
        GrimoraCliRunner.DeleteInstance(instance);
        Assert.False(Directory.Exists(dir));
    }
}
