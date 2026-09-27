using Microsoft.Data.Sqlite;

namespace Grimora.Store.Data;

/// <summary>Compat shim (slice 32c): the store folder was <c>~/.aitm</c>; it is <c>~/.grimora</c> now. Runs at
/// server start, and only when the new folder is missing. The old folder is never renamed, deleted or written:
/// it stays as it is and is itself the backup. Each database is copied with the SQLite backup API (consistent
/// even while an old server has it open); other small files are copied plainly. Everything goes into
/// <c>.grimora.partial</c> and one directory move makes it <c>.grimora</c>. A lock file around the whole move
/// lets two starting processes agree: one moves, the other waits and then sees the finished folder. Nothing
/// here throws into server startup.</summary>
public static class LegacyStore
{
    private const int PollMilliseconds = 200;
    private const int MaxWaitMilliseconds = 60_000;

    public static bool MoveIfNeeded(string home, TextWriter? log = null) =>
        MoveIfNeeded(home, log, MaxWaitMilliseconds);

    internal static bool MoveIfNeeded(string home, TextWriter? log, int maxWaitMilliseconds)
    {
        string oldDir = Path.Combine(home, ".aitm");
        string newDir = Path.Combine(home, ".grimora");
        try
        {
            if (!Directory.Exists(oldDir) || Directory.Exists(newDir)) return false;

            using FileStream? gate = TakeLock(Path.Combine(home, ".grimora.move.lock"), newDir, maxWaitMilliseconds);
            if (gate is null) return false; // the folder appeared while waiting, or the holder is still busy
            if (Directory.Exists(newDir)) return false; // the previous holder finished

            string partial = newDir + ".partial";
            try
            {
                if (Directory.Exists(partial)) Directory.Delete(partial, true); // a crashed run's leftover; we hold the lock
                CopyTree(oldDir, partial, log);
                File.WriteAllText(Path.Combine(partial, ".migrated-from-aitm"),
                    $"source={oldDir}{Environment.NewLine}utc={DateTime.UtcNow:o}{Environment.NewLine}");
                SqliteConnection.ClearAllPools();
                Directory.Move(partial, newDir);
                return true;
            }
            catch (Exception e)
            {
                log?.WriteLine($"grimora: could not move the old store {oldDir}: {e.Message}; the old folder is untouched.");
                try { if (Directory.Exists(partial)) Directory.Delete(partial, true); }
                catch (Exception) { /* best effort; the next lock holder cleans it */ }
                return false;
            }
        }
        catch (Exception e)
        {
            log?.WriteLine($"grimora: the store move did not start: {e.Message}");
            return false;
        }
    }

    /// <summary>Returns the held lock, or null when it could not be taken within the wait and the new folder
    /// exists (or the wait ran out). A waiter never touches a <c>.partial</c> folder.</summary>
    private static FileStream? TakeLock(string lockPath, string newDir, int maxWaitMilliseconds)
    {
        int waited = 0;
        while (true)
        {
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
            }
            catch (IOException)
            {
                if (Directory.Exists(newDir) || waited >= maxWaitMilliseconds) return null;
                Thread.Sleep(PollMilliseconds);
                waited += PollMilliseconds;
            }
        }
    }

    private static void CopyTree(string from, string to, TextWriter? log)
    {
        Directory.CreateDirectory(to);
        foreach (string file in Directory.GetFiles(from))
        {
            string name = Path.GetFileName(file);
            if (name.EndsWith("-wal", StringComparison.Ordinal) || name.EndsWith("-shm", StringComparison.Ordinal)
                || name.EndsWith("-journal", StringComparison.Ordinal))
                continue; // the backup API carries the WAL content
            if (name.EndsWith(".db", StringComparison.Ordinal))
                BackupDatabase(file, Path.Combine(to, name.Replace("aitm", "grimora", StringComparison.OrdinalIgnoreCase)));
            else
                CopySmallFile(file, Path.Combine(to, name), log);
        }

        foreach (string dir in Directory.GetDirectories(from))
            CopyTree(dir, Path.Combine(to, Path.GetFileName(dir)), log);
    }

    private static void BackupDatabase(string source, string target)
    {
        using SqliteConnection src = new($"Data Source={source};Mode=ReadOnly;Pooling=False");
        using SqliteConnection dst = new($"Data Source={target};Pooling=False");
        src.Open();
        dst.Open();
        src.BackupDatabase(dst);
    }

    private static void CopySmallFile(string source, string target, TextWriter? log)
    {
        try
        {
            using FileStream from = new(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using FileStream to = new(target, FileMode.Create, FileAccess.Write, FileShare.None);
            from.CopyTo(to);
        }
        catch (IOException e)
        {
            log?.WriteLine($"grimora: skipped {source} (in use): {e.Message}");
        }
    }
}
