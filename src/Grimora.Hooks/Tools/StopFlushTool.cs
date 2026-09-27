using System.Text.Json;
using Grimora.Brain.Tools;
using Grimora.Hooks.Data;
using Microsoft.Data.Sqlite;

namespace Grimora.Hooks.Tools;

/// <summary>
/// Stop handler: the durability guarantee SKILL.md promises for staged learning ("staged learning can't
/// be silently dropped") had no hook enforcing it — hooks.json had no Stop entry at all, so a session
/// could end with entries sitting in <c>pending-learn.jsonl</c> (see <see cref="BrainStageTool"/>) that
/// nobody ever ran <c>grimora flush</c>/<c>brain_flush</c> on, and nothing said so.
///
/// This tool runs on every Stop: if the resolved instance has no ledger, or an empty one, it is silent
/// (the common case). If the ledger has entries, it tries to flush them the same way <see cref="BrainFlushTool"/>
/// does; on success it says so, and on failure (no store yet, a locked database, a malformed line) it
/// warns instead of staying quiet, naming the ledger path so a human can act on it. Always fails open —
/// a hook must never block or crash a session — but "fail open" here means "warn and let the session end
/// anyway", not "say nothing".
/// </summary>
public static class StopFlushTool
{
    public static string Execute(string stdin) => Execute(stdin, projectDir: null);

    public static string Execute(string stdin, string? projectDir)
    {
        string instance;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(stdin) ? "{}" : stdin);
            JsonElement payload = doc.RootElement;
            string? cwd = payload.TryGetProperty("cwd", out JsonElement cwdEl) && cwdEl.ValueKind == JsonValueKind.String
                ? cwdEl.GetString()
                : null;
            instance = HookPaths.ResolveInstance(cwd, projectDir);
        }
        catch (JsonException)
        {
            // A malformed payload means Claude Code itself sent something unreadable — there is no
            // instance to check a ledger for, so this is the same "never blocks, says nothing" case
            // every other hook handler treats a bad payload as (e.g. SessionIndexChatTool).
            return "";
        }

        try
        {
            string ledger = Path.Combine(HookPaths.InstanceDir(instance), "pending-learn.jsonl");
            if (!File.Exists(ledger)) return "";
            string[] pending = [.. File.ReadAllLines(ledger).Where(l => l.Trim().Length > 0)];
            if (pending.Length == 0) return "";

            string dbPath = HookPaths.DbPath(instance);
            if (!File.Exists(dbPath))
                return Warn(pending.Length, ledger, "the instance has no store yet, so there is nothing to flush into");

            using SqliteConnection connection = HookStore.Open(dbPath);
            string result = new BrainFlushTool().ExecuteCli(connection);
            return result.StartsWith("flushed ", StringComparison.Ordinal)
                ? $"grimora: {result}"
                : Warn(pending.Length, ledger, result);
        }
        catch (Exception ex)
        {
            // Fail open (never block Stop), but never silently — a caught exception here (a locked
            // store, a malformed ledger line) is exactly the "something kept this from flushing" case
            // the durability guarantee exists for.
            return $"grimora WARNING: could not check for unflushed staged learnings ({ex.Message}).";
        }
    }

    private static string Warn(int count, string ledger, string reason) =>
        $"grimora WARNING: {count} staged learning(s) in {ledger} were not flushed ({reason}). " +
        "Run `grimora flush` (or the brain_flush MCP tool) before they are lost.";
}
