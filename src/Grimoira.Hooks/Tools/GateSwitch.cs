using System.Globalization;
using Grimoira.Hooks.Data;

namespace Grimoira.Hooks.Tools;

/// <summary>
/// The pause switch for the gates that inject context (the PreToolUse edit gate and the UserPromptSubmit
/// prompt recall): <c>grimoira gates off [reason]</c> writes a marker in the instance folder and both
/// gates print nothing while it is there; <c>gates on</c> removes it. A marker older than
/// <see cref="MaxPause"/> no longer counts, so a forgotten pause cannot outlive a working day. Every flip
/// is appended to <c>gates.log</c>, the live proof of who turned what off and when. Fails open: any IO
/// error reads as "on", never as "off". The PostToolUse pattern counter is not a gate (it injects
/// nothing) and keeps counting while paused.
/// </summary>
public static class GateSwitch
{
    public static readonly TimeSpan MaxPause = TimeSpan.FromHours(12);

    private const string OffLine = "Grimoira gates off (edit gate, prompt recall) until `grimoira gates on` or 12 h.";
    private const string OnLine = "Grimoira gates on.";

    public static string PausePath(string instance) => Path.Combine(HookPaths.InstanceDir(instance), "gates.paused");

    public static string LogPath(string instance) => Path.Combine(HookPaths.InstanceDir(instance), "gates.log");

    /// <summary>Writes the marker <c>&lt;utc iso&gt;|&lt;reason&gt;</c> and logs the flip.</summary>
    public static string Off(string instance, string reason)
    {
        string now = Now();
        string path = PausePath(instance);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, $"{now}|{reason}");
        Log(instance, $"{now} off {reason}");
        return OffLine;
    }

    /// <summary>Removes the marker (also when there is none) and logs the flip.</summary>
    public static string On(string instance)
    {
        string path = PausePath(instance);
        if (File.Exists(path)) File.Delete(path);
        Log(instance, $"{Now()} on");
        return OnLine;
    }

    /// <summary>True while a marker younger than <see cref="MaxPause"/> exists. An older or unreadable
    /// marker counts as on and is removed best effort, with an "on (expired)" log line.</summary>
    public static bool IsPaused(string instance)
    {
        try
        {
            string path = PausePath(instance);
            if (!File.Exists(path)) return false;
            DateTime? since = Since(File.ReadAllText(path));
            if (since is not null && DateTime.UtcNow - since.Value < MaxPause) return true;
            Expire(instance, path);
            return false;
        }
        catch
        {
            // fail open: a switch that cannot be read leaves the gates on
            return false;
        }
    }

    public static string Status(string instance)
    {
        if (!IsPaused(instance)) return "Grimoira gates: on";
        string[] parts = File.ReadAllText(PausePath(instance)).Trim().Split('|', 2);
        string reason = parts.Length > 1 ? parts[1] : "";
        return $"Grimoira gates: off since {parts[0]} ({reason})";
    }

    private static DateTime? Since(string marker)
    {
        string stamp = marker.Trim().Split('|', 2)[0];
        return DateTime.TryParse(stamp, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime since)
            ? since
            : null;
    }

    private static void Expire(string instance, string path)
    {
        try
        {
            File.Delete(path);
            Log(instance, $"{Now()} on (expired)");
        }
        catch
        {
            // best effort: an expired marker that stays behind is ignored on the next read anyway
        }
    }

    private static string Now() => DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);

    private static void Log(string instance, string line)
    {
        try
        {
            string path = LogPath(instance);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path, line + "\n");
        }
        catch
        {
            // best effort
        }
    }
}
