using Microsoft.Data.Sqlite;

namespace Grimoira.Store.Data;

/// <summary>Compat shim (slice 32c): the store folder was <c>~/.aitm</c>, then <c>~/.grimora</c>; it is
/// <c>~/.grimoira</c> now. Runs at server start, and in the file-based entry points (grimoira.cs, mcp.cs), and
/// only while the new folder holds no migration marker and no instance database (an early log or an empty
/// instance folder made by a hook does not count as a store). The source is
/// <c>~/.grimora</c> when it exists (it is the newer), else <c>~/.aitm</c>. The old folders are never renamed,
/// deleted or written: they stay as they are and are themselves the backup. Each database is copied with the SQLite backup API (consistent
/// even while an old server has it open); other small files are copied plainly. Everything goes into
/// <c>.grimoira.partial</c> and one directory move makes it <c>.grimoira</c>. When <c>.grimoira</c> already exists
/// (early files only), the finished partial is merged into it: files that are not there yet are moved in
/// (databases after the other files, the marker last); a file that exists in both stays as it is, and the old
/// store's copy goes beside it as <c>name.from-grimora</c> (or <c>.from-aitm</c>), so nothing is lost or
/// overwritten. A lock file around the whole move
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
        string newDir = Path.Combine(home, ".grimoira");
        string oldDir = Path.Combine(home, ".grimora");
        string sourceName = "grimora";
        if (!Directory.Exists(oldDir))
        {
            oldDir = Path.Combine(home, ".aitm");
            sourceName = "aitm";
        }

        try
        {
            if (!Directory.Exists(oldDir) || !NeedsMove(newDir)) return false;

            using FileStream? gate = TakeLock(Path.Combine(home, ".grimoira.move.lock"), newDir, maxWaitMilliseconds);
            if (gate is null) return false; // the move finished while waiting, or the holder is still busy
            if (!NeedsMove(newDir)) return false; // the previous holder finished

            string partial = newDir + ".partial";
            try
            {
                if (Directory.Exists(partial)) Directory.Delete(partial, true); // a crashed run's leftover; we hold the lock
                CopyTree(oldDir, partial, log);
                File.WriteAllText(Path.Combine(partial, ".migrated-from-" + sourceName),
                    $"source={oldDir}{Environment.NewLine}utc={DateTime.UtcNow:o}{Environment.NewLine}");
                if (Directory.Exists(newDir)) MergeInto(partial, newDir, sourceName);
                else Directory.Move(partial, newDir);
                return true;
            }
            catch (Exception e)
            {
                log?.WriteLine($"grimoira: could not move the old store {oldDir}: {e.Message}; the old folder is untouched.");
                try { if (Directory.Exists(partial)) Directory.Delete(partial, true); }
                catch (Exception) { /* best effort; the next lock holder cleans it */ }
                return false;
            }
        }
        catch (Exception e)
        {
            log?.WriteLine($"grimoira: the store move did not start: {e.Message}");
            return false;
        }
    }

    /// <summary>The new folder is a finished store when it holds a <c>.migrated-from-*</c> marker or an instance
    /// database (<c>grimoira.db</c> in any subfolder). A missing folder, or one with only logs and empty folders,
    /// still needs the move.</summary>
    private static bool NeedsMove(string newDir)
    {
        if (!Directory.Exists(newDir)) return true;
        return !Directory.EnumerateFiles(newDir, ".migrated-from-*", SearchOption.TopDirectoryOnly).Any()
            && !Directory.EnumerateFiles(newDir, "grimoira.db", SearchOption.AllDirectories).Any();
    }

    private static void MergeInto(string from, string to, string sourceName)
    {
        string[] files = Directory.GetFiles(from, "*", SearchOption.AllDirectories);
        foreach (string dir in Directory.GetDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));

        // Plain files first, then databases, then the marker: a crash leaves the marker out, and the old folder untouched.
        foreach (string file in files.OrderBy(f => MergeRank(Path.GetFileName(f))))
        {
            string target = Path.Combine(to, Path.GetRelativePath(from, file));
            if (File.Exists(target))
            {
                if (File.ReadAllBytes(target).AsSpan().SequenceEqual(File.ReadAllBytes(file))) continue;
                target += ".from-" + sourceName;
                if (File.Exists(target)) continue; // an earlier crashed merge already placed it
            }

            File.Move(file, target);
        }

        Directory.Delete(from, true);
    }

    private static int MergeRank(string name) =>
        name.StartsWith(".migrated-from-", StringComparison.Ordinal) ? 2 : name.EndsWith(".db", StringComparison.Ordinal) ? 1 : 0;

    /// <summary>Returns the held lock, or null when it could not be taken within the wait and the move is done
    /// (or the wait ran out). A waiter never touches a <c>.partial</c> folder.</summary>
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
                if (!NeedsMove(newDir) || waited >= maxWaitMilliseconds) return null;
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
                BackupDatabase(file, Path.Combine(to, name
                    .Replace("aitm", "grimoira", StringComparison.OrdinalIgnoreCase)
                    .Replace("grimora", "grimoira", StringComparison.OrdinalIgnoreCase)));
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
            log?.WriteLine($"grimoira: skipped {source} (in use): {e.Message}");
        }
    }
}
