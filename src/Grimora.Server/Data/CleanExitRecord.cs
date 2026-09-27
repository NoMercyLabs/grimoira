using System.Globalization;

namespace Grimora.Server.Data;

/// <summary>What a client may conclude about a call whose connection failed with no answer.</summary>
public enum LostCallVerdict
{
    /// <summary>The service that took the call is still there (or cannot be told apart): the original failure stands.</summary>
    Unknown,

    /// <summary>The service that took the call exited cleanly and never started it: resend.</summary>
    NeverRan,

    /// <summary>The service exited while the call was in flight and cannot prove it did not start it: never resend.</summary>
    MayHaveRun,
}

/// <summary>
/// What the last service run left in the data directory when it stopped: when it held server.lock, whether it
/// drained every call within its limit (<see cref="Clean"/>), and the ids (<c>X-Grimora-Call</c>) of the calls it
/// started, from a ring of the last 256 (CallRing). A stopping service closes a connection whose
/// request it never read, and on Windows a client can be connected to a pipe instance the service never read from,
/// so a call can be lost however the stop is ordered. The client resends such a call only when this record proves
/// the call never started (<see cref="Judge"/>). The service deletes the record when it starts
/// (<see cref="DeleteStale"/>), so a record always belongs to the last run. Shared with Grimora.Cli as a linked file.
/// </summary>
public sealed record CleanExitRecord(DateTime StartedUtc, DateTime ExitedUtc, bool Clean, bool Wrapped, DateTime OldestKeptStartUtc, IReadOnlyList<string> CallIds)
{
    public const string FileName = "server.exit";

    /// <summary>Writes the record (replacing any earlier one in one step).</summary>
    public void Write(string dataDir)
    {
        string path = Path.Combine(dataDir, FileName);
        string temp = path + ".tmp";
        string head = string.Create(CultureInfo.InvariantCulture,
            $"{StartedUtc.Ticks} {ExitedUtc.Ticks} {(Clean ? 1 : 0)} {(Wrapped ? 1 : 0)} {OldestKeptStartUtc.Ticks}");
        File.WriteAllLines(temp, [head, .. CallIds]);
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>The record of the last run, or null when there is none (or it cannot be read).</summary>
    public static CleanExitRecord? Read(string dataDir)
    {
        try
        {
            string[] lines = File.ReadAllLines(Path.Combine(dataDir, FileName));
            string[] head = lines.Length > 0 ? lines[0].Split(' ') : [];
            if (head.Length != 5) return null;
            long[] n = new long[5];
            for (int i = 0; i < 5; i++)
                if (!long.TryParse(head[i], NumberStyles.None, CultureInfo.InvariantCulture, out n[i])) return null;
            return new CleanExitRecord(new DateTime(n[0], DateTimeKind.Utc), new DateTime(n[1], DateTimeKind.Utc), n[2] == 1, n[3] == 1,
                new DateTime(n[4], DateTimeKind.Utc), [.. lines.Skip(1).Where(l => l.Length > 0)]);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Removes the record of an earlier run; the service calls this once it holds server.lock.</summary>
    public static void DeleteStale(string dataDir)
    {
        try { File.Delete(Path.Combine(dataDir, FileName)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* a record that stays is judged by its times */ }
    }

    /// <summary>For a call with id <paramref name="callId"/> sent at <paramref name="sentUtc"/> to the service that has
    /// since freed server.lock. Only one service holds the lock at a time, so a record with
    /// StartedUtc &lt;= sentUtc &lt;= ExitedUtc is of the service the call went to. The call never ran only when that
    /// service drained cleanly, did not start the id, and its ring still reaches back to the send time.</summary>
    public static LostCallVerdict Judge(CleanExitRecord? record, DateTime sentUtc, string callId)
    {
        if (record is null) return LostCallVerdict.MayHaveRun;
        if (sentUtc < record.StartedUtc || sentUtc > record.ExitedUtc) return LostCallVerdict.MayHaveRun;
        if (!record.Clean || record.CallIds.Contains(callId)) return LostCallVerdict.MayHaveRun;
        if (record.Wrapped && sentUtc <= record.OldestKeptStartUtc) return LostCallVerdict.MayHaveRun;
        return LostCallVerdict.NeverRan;
    }
}
