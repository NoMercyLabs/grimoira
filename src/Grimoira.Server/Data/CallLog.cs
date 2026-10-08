using System.Diagnostics;
using System.Text.Json;

namespace Grimoira.Server.Data;

/// <summary>Best-effort, bounded per-project call timings. Logging must never fail a tool call.</summary>
internal static class CallLog
{
    private const long MaxBytes = 1024 * 1024;
    private static readonly Lock Gate = new();

    public static void Record(ProjectHandle handle, string name, string instance, long waitedMs, long heldMs)
    {
        try
        {
            string line = JsonSerializer.Serialize(new
            {
                name, instance, waited_ms = waitedMs, held_ms = heldMs,
            }) + Environment.NewLine;
            lock (Gate)
            {
                string path = handle.CallsLogPath;
                FileInfo info = new(path);
                if (info.Exists && info.Length + System.Text.Encoding.UTF8.GetByteCount(line) > MaxBytes)
                    File.WriteAllText(path, "");
                File.AppendAllText(path, line);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    public static long Milliseconds(long start) => (long)Stopwatch.GetElapsedTime(start).TotalMilliseconds;
}
