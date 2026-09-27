using System.Globalization;

namespace Grimora.Server.Data;

/// <summary>
/// Proof, left in the data directory, that the last service to exit answered every call it started. Written by
/// the service as the last step of a clean stop (all calls drained, none cut off by the shutdown timeout), before
/// it frees server.lock. A stopping service closes connections whose request it never read, and on Windows a
/// client can be connected to a pipe instance the service never read from, so a call can be lost however the stop
/// is ordered. Such a call never ran; a client that lost its call resends it only when this record covers the
/// moment it was sent (<see cref="Covers"/>). Shared with Grimora.Cli as a linked file.
/// </summary>
public static class CleanExitRecord
{
    public const string FileName = "server.exit";

    /// <summary>Records a clean exit of the service that held server.lock from <paramref name="startedUtc"/> to now.</summary>
    public static void Write(string dataDir, DateTime startedUtc, DateTime exitedUtc)
    {
        string path = Path.Combine(dataDir, FileName);
        string temp = path + ".tmp";
        File.WriteAllText(temp, string.Create(CultureInfo.InvariantCulture, $"{startedUtc.Ticks} {exitedUtc.Ticks}"));
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>True when the last clean exit was of the service that held server.lock at <paramref name="sentUtc"/>
    /// (it started before and exited after): only one service holds the lock at a time, so that is the service the
    /// call went to, and it answered every call it started.</summary>
    public static bool Covers(string dataDir, DateTime sentUtc)
    {
        try
        {
            string[] parts = File.ReadAllText(Path.Combine(dataDir, FileName)).Split(' ');
            return parts.Length == 2
                && long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out long started)
                && long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out long exited)
                && started <= sentUtc.Ticks && sentUtc.Ticks <= exited;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
