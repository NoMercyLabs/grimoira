using System.Text.Json;
using System.Text.RegularExpressions;
using Grimora.Hooks.Data;
using Grimora.Store.Data;

namespace Grimora.Hooks.Tools;

/// <summary>
/// UserPromptSubmit handler, ported verbatim from compact-restore.mjs (RESTRUCTURE.md slice 20, "Hooks,
/// part 1"): hands back the anchors <see cref="CompactBriefTool"/> wrote before the last compaction. The
/// brief and its ledger wait on disk and land on the first prompt after the compaction, once — a marker
/// file next to the brief (not deletion: the brief and ledger are kept on disk for later reference) makes
/// a second call after the same compaction return an empty string, same as the .mjs's fail-open catch
/// plus <c>process.exit(0)</c>.
/// </summary>
public static partial class CompactRestoreTool
{
    public static string Execute(string stdin) => Execute(stdin, projectDir: null);

    /// <param name="projectDir">The project the caller resolved (<see cref="HookPaths.ProjectDir"/>), or null.</param>
    public static string Execute(string stdin, string? projectDir)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(stdin) ? "{}" : stdin);
            JsonElement payload = doc.RootElement;

            string? cwd = GetString(payload, "cwd");
            string? sessionId = GetString(payload, "session_id");
            string? transcriptPath = GetString(payload, "transcript_path");

            string instance = HookPaths.ResolveInstance(cwd, projectDir);
            string path = HookPaths.BriefPath(instance, sessionId);
            string restoredMarkerPath = path + ".restored";
            if (!File.Exists(path) || File.Exists(restoredMarkerPath)) return "";

            string brief = File.ReadAllText(path);
            if (brief.Trim().Length < 40) return "";

            // Mark, never delete: the brief and its ledger stay on disk (the owner can still open them), but
            // this compaction's restore only fires once.
            try { File.WriteAllText(restoredMarkerPath, ""); } catch { /* best effort */ }

            string lossPrefix = LossPrefix(HookPaths.LedgerPath(instance, sessionId), transcriptPath);

            var envelope = new
            {
                hookSpecificOutput = new
                {
                    hookEventName = "UserPromptSubmit",
                    additionalContext =
                        $"{lossPrefix}{brief}\n\nThese are facts recorded at compaction time, not a plan. Continue the work in " +
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

    private static string? GetString(JsonElement e, string prop) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out JsonElement v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    // A best-effort check, not a hard gate: if the ledger cannot be read or the transcript is already
    // gone, this says nothing rather than blocking the restore. "Ledger has fewer the owner entries than the
    // transcript has user turns" is the only signal available without re-running the same filtering the
    // ledger already applied, so a legitimate gap (noise the ledger correctly dropped) can also trigger
    // it — it is a prompt to look, not proof of loss.
    private static string LossPrefix(string ledgerPath, string? transcriptPath)
    {
        try
        {
            if (transcriptPath is null || !File.Exists(transcriptPath) || !File.Exists(ledgerPath)) return "";

            int transcriptUserCount = 0;
            foreach (string line in File.ReadAllLines(transcriptPath))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    using JsonDocument d = JsonDocument.Parse(line);
                    if (GetString(d.RootElement, "type") == "user") transcriptUserCount++;
                }
                catch
                {
                    // partial write
                }
            }

            int ledgerOwnerCount = OwnerLedgerLine().MatchesOrEmpty(File.ReadAllText(ledgerPath)).Count;
            if (ledgerOwnerCount >= transcriptUserCount) return "";

            return $"LEDGER LOSS: ledger {ledgerOwnerCount} of transcript {transcriptUserCount}\n\n";
        }
        catch
        {
            return "";
        }
    }

    [GeneratedRegex(@"^- \[[^\]]*\] the owner", RegexOptions.Multiline, RegexTimeout.Milliseconds)]
    private static partial Regex OwnerLedgerLine();
}
