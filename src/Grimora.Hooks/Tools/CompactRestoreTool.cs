using System.Text.Json;
using Grimora.Hooks.Data;

namespace Grimora.Hooks.Tools;

/// <summary>
/// UserPromptSubmit handler, ported verbatim from compact-restore.mjs (RESTRUCTURE.md slice 20, "Hooks,
/// part 1"): hands back the anchors <see cref="CompactBriefTool"/> wrote before the last compaction. The
/// brief waits on disk and lands on the first prompt after the compaction, once — a second call after
/// the file is gone (whether never written, or already consumed) returns an empty string, same as the
/// .mjs's fail-open catch plus <c>process.exit(0)</c>.
/// </summary>
public static class CompactRestoreTool
{
    public static string Execute(string stdin) => Execute(stdin, projectDir: null);

    /// <param name="projectDir">The project the caller resolved (<see cref="HookPaths.ProjectDir"/>), or null.</param>
    public static string Execute(string stdin, string? projectDir)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(stdin) ? "{}" : stdin);
            JsonElement payload = doc.RootElement;

            string? cwd = payload.ValueKind == JsonValueKind.Object
                && payload.TryGetProperty("cwd", out JsonElement cwdEl)
                && cwdEl.ValueKind == JsonValueKind.String
                    ? cwdEl.GetString()
                    : null;
            string? sessionId = payload.ValueKind == JsonValueKind.Object
                && payload.TryGetProperty("session_id", out JsonElement sidEl)
                && sidEl.ValueKind == JsonValueKind.String
                    ? sidEl.GetString()
                    : null;

            string instance = HookPaths.ResolveInstance(cwd, projectDir);
            string path = HookPaths.BriefPath(instance, sessionId);
            if (!File.Exists(path)) return "";

            string brief = File.ReadAllText(path);
            // One-shot: a second compaction writes its own brief, and a stale one describes the wrong
            // turn.
            try { File.Delete(path); } catch { /* best effort */ }
            if (brief.Trim().Length < 40) return "";

            var envelope = new
            {
                hookSpecificOutput = new
                {
                    hookEventName = "UserPromptSubmit",
                    additionalContext =
                        $"{brief}\n\nThese are facts recorded at compaction time, not a plan. Continue the work in " +
                        "progress; do not re-derive this state by reading files, and do not restate it back to the owner.",
                },
            };
            return JsonSerializer.Serialize(envelope);
        }
        catch
        {
            // fail open
            return "";
        }
    }
}
