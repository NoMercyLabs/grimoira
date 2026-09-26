using Aitm.Store.Data;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Aitm.Server.Data;

/// <summary>
/// Ported from process-owner.mjs (NoMercy pilot): record newly launched children. Never adopt or kill
/// existing PIDs. Becomes the server's single-instance lock and pid file (RESTRUCTURE.md slice 23,
/// section 2.3). Field names match the .mjs record shape verbatim, since the on-disk JSON is the
/// contract other tooling (and the ported tests) read.
/// </summary>
public sealed record OwnerRecord
{
    [JsonPropertyName("version")] public int Version { get; init; } = 1;
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("workspace")] public required string Workspace { get; init; }
    [JsonPropertyName("cwd")] public required string Cwd { get; init; }
    [JsonPropertyName("owner")] public required OwnerInfo Owner { get; init; }
    [JsonPropertyName("application_session")] public string? ApplicationSession { get; init; }
    [JsonPropertyName("capability")] public string Capability { get; init; } = "aitm-tool-server";
    [JsonPropertyName("launched_at")] public required string LaunchedAt { get; init; }
    [JsonPropertyName("child_pid")] public int? ChildPid { get; set; }
    [JsonPropertyName("state")] public string State { get; set; } = "starting";
    [JsonPropertyName("exit_code")] public int? ExitCode { get; set; }
    [JsonPropertyName("signal")] public string? Signal { get; set; }
    [JsonPropertyName("ended_at")] public string? EndedAt { get; set; }
    [JsonPropertyName("cancellation")] public string Cancellation { get; init; } = "Only the live owning launcher may signal its original child handle.";
}

public sealed record OwnerInfo(
    [property: JsonPropertyName("pid")] int Pid,
    [property: JsonPropertyName("parent_pid")] int ParentPid,
    [property: JsonPropertyName("started_at")] string StartedAt);

public sealed class OwnedProcess
{
    public required Process Process { get; init; }
    public required string Id { get; init; }
    public required string FileName { get; init; }

    /// <summary>Completes once the final state (exited or failed) is saved to <see cref="FileName"/>.</summary>
    public required Task Recorded { get; init; }

    /// <summary>No PID lookup, tree walk, or persisted record is used as kill authority.</summary>
    public bool Cancel()
    {
        try
        {
            if (Process.HasExited) return false;
            Process.Kill();
            return true;
        }
        catch (InvalidOperationException)
        {
            // Either never started (failed spawn) or already exited underneath us.
            return false;
        }
    }
}

public static partial class ProcessOwner
{

    /// <summary>Null when <paramref name="project"/> is not inside <paramref name="workspace"/>.</summary>
    public static string? PilotRoot(string project, string workspace)
    {
        try
        {
            string root = Path.TrimEndingDirectorySeparator(new DirectoryInfo(workspace).FullName);
            string child = Path.TrimEndingDirectorySeparator(new DirectoryInfo(project).FullName);
            string rel = Path.GetRelativePath(root, child);
            bool inside = rel == "." || (!rel.StartsWith("..") && !Path.IsPathRooted(rel));
            return inside ? root : null;
        }
        catch
        {
            return null;
        }
    }

    private static string Directory_(string root) => Path.Combine(root, ".claude", "scratch", "process-owners");

    /// <summary>Bound housekeeping work. Running and ambiguous records are never removed.</summary>
    public static void TrimCompleted(string root, int keep = 50)
    {
        string directory = Directory_(root);
        List<(string FileName, string Time)> completed = [];
        try
        {
            if (!Directory.Exists(directory)) return;
            foreach (string filename in Directory.EnumerateFiles(directory).Take(1000))
            {
                string name = Path.GetFileName(filename);
                if (!ProcessRecordFileName().IsMatch(name)) continue;
                try
                {
                    using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(filename));
                    JsonElement root2 = doc.RootElement;
                    if (root2.TryGetProperty("version", out JsonElement v) && v.GetInt32() == 1
                        && root2.TryGetProperty("state", out JsonElement s)
                        && (s.GetString() == "exited" || s.GetString() == "failed")
                        && root2.TryGetProperty("ended_at", out JsonElement ended)
                        && ended.GetString() is { } endedAt)
                    {
                        completed.Add((filename, endedAt));
                    }
                }
                catch
                {
                    // Keep unreadable records for inspection.
                }
            }
            completed.Sort((a, b) => string.CompareOrdinal(b.Time, a.Time));
            foreach ((string filename, _) in completed.Skip(keep)) File.Delete(filename);
        }
        catch
        {
            // Retention never prevents launching or stopping a child.
        }
    }

    public static OwnedProcess LaunchOwned(string command, IReadOnlyList<string> args, string root, string cwd)
    {
        string id = Guid.NewGuid().ToString();
        string directory = Directory_(root);
        string filename = Path.Combine(directory, $"{id}.json");
        Process process = new()
        {
            StartInfo = new ProcessStartInfo(command)
            {
                WorkingDirectory = cwd,
                UseShellExecute = false,
            },
            EnableRaisingEvents = true,
        };
        foreach (string a in args) process.StartInfo.ArgumentList.Add(a);

        TaskCompletionSource recorded = new(TaskCreationOptions.RunContinuationsAsynchronously);
        DateTime startedAt = DateTime.UtcNow;
        OwnerRecord record = new()
        {
            Id = id,
            Workspace = root,
            Cwd = Path.GetFullPath(cwd),
            Owner = new OwnerInfo(Environment.ProcessId, 0, startedAt.ToString("o")),
            LaunchedAt = startedAt.ToString("o"),
        };

        void Save()
        {
            try
            {
                Directory.CreateDirectory(directory);
                string temp = $"{filename}.tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(record, new JsonSerializerOptions { WriteIndented = true }) + "\n");
                File.Move(temp, filename, overwrite: true);
            }
            catch
            {
                // Best effort: a missing ownership record never blocks launch or stop.
            }
        }

        // Attached before Start: a child that exits before the handler is attached never raises it.
        // The lock keeps the "running" write after Start from overwriting an "exited" that won the race.
        object gate = new();
        process.Exited += (_, _) =>
        {
            lock (gate)
            {
                record.ChildPid ??= process.Id;
                record.State = "exited";
                record.ExitCode = process.ExitCode;
                record.EndedAt = DateTime.UtcNow.ToString("o");
                Save();
            }
            TrimCompleted(root);
            recorded.TrySetResult();
        };

        Save();
        try
        {
            process.Start();
            lock (gate)
            {
                record.ChildPid = process.Id;
                if (record.State != "exited")
                {
                    record.State = "running";
                    Save();
                }
            }
        }
        catch
        {
            record.State = "failed";
            record.EndedAt = DateTime.UtcNow.ToString("o");
            Save();
            TrimCompleted(root);
            recorded.TrySetResult();
            return new OwnedProcess { Process = process, Id = id, FileName = filename, Recorded = recorded.Task };
        }

        return new OwnedProcess { Process = process, Id = id, FileName = filename, Recorded = recorded.Task };
    }

    /// <summary>Inspection only. A running record after a crash is not proof of a live owner.</summary>
    public static (bool Limited, List<Dictionary<string, object?>> Records) InspectOwners(string root, int limit = 100)
    {
        string directory = Directory_(root);
        if (!Directory.Exists(directory)) return (false, []);
        string[] files = [.. Directory.EnumerateFiles(directory).Where(f => ProcessRecordFileName().IsMatch(Path.GetFileName(f)))];
        List<Dictionary<string, object?>> records = [];
        foreach (string file in files.Take(limit))
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(file));
                JsonElement r = doc.RootElement;
                records.Add(new Dictionary<string, object?>
                {
                    ["id"] = r.TryGetProperty("id", out JsonElement id) ? id.GetString() : null,
                    ["owner_pid"] = r.TryGetProperty("owner", out JsonElement owner) && owner.TryGetProperty("pid", out JsonElement pid) ? pid.GetInt32() : null,
                    ["child_pid"] = r.TryGetProperty("child_pid", out JsonElement cp) && cp.ValueKind != JsonValueKind.Null ? cp.GetInt32() : null,
                    ["state"] = r.TryGetProperty("state", out JsonElement st) ? st.GetString() : null,
                    ["launched_at"] = r.TryGetProperty("launched_at", out JsonElement la) ? la.GetString() : null,
                    ["liveness"] = "not verified; record is not permission to terminate a PID",
                });
            }
            catch
            {
                records.Add(new Dictionary<string, object?> { ["file"] = Path.GetFileName(file), ["state"] = "unreadable" });
            }
        }
        return (files.Length > limit, records);
    }

    /// <summary>
    /// Single-instance guard for the server itself (RESTRUCTURE.md slice 25: "Single instance: reuse
    /// process-owner (slice 23a); a second copy exits non-zero with a clear message."). This is a
    /// different job from <see cref="LaunchOwned"/> above (which tracks children this process spawns):
    /// here the server is guarding itself, by holding an exclusive OS-level lock on one file per data
    /// directory for as long as it runs. A second process pointed at the same data directory gets a
    /// sharing violation and <c>null</c> back; it never adopts or inspects the first process's PID.
    /// Returns the open handle on success — the caller keeps it open for the process lifetime and lets
    /// the OS release it on exit; returns <c>null</c> when another instance already holds it.
    /// </summary>
    public static FileStream? TryAcquireSingleInstanceLock(string dataDir)
    {
        Directory.CreateDirectory(dataDir);
        string lockPath = Path.Combine(dataDir, "server.lock");
        try
        {
            return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            return null;
        }
    }

    [GeneratedRegex(@"^[a-f0-9-]{36}\.json$", RegexOptions.None, RegexTimeout.Milliseconds)]
    private static partial Regex ProcessRecordFileName();
}
