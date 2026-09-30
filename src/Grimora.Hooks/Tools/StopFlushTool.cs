using System.Text.Json;
using Grimora.Brain.Tools;
using Grimora.Hooks.Data;
using Microsoft.Data.Sqlite;
using static Grimora.Store.Data.JsonShape;

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
///
/// Since <see cref="BrainStageTool.LedgerPath"/> keys a ledger by session, <see cref="BrainFlushTool.ExecuteCli"/>
/// (given this session's own id, from the hook payload's <c>session_id</c>) already checks this session's
/// own ledger first and falls back to the legacy shared one any session-unaware caller (a bare CLI stage
/// with no <c>--session</c>, or an entry staged before this session existed) still writes to — never
/// another still-running session's own ledger, which is exactly the isolation the session keying exists
/// for. This tool only decides whether the outcome is worth reporting.
/// </summary>
public static class StopFlushTool
{
    public static string Execute(string stdin) => Execute(stdin, projectDir: null);

    public static string Execute(string stdin, string? projectDir)
    {
        string instance;
        string sessionId;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(stdin) ? "{}" : stdin);
            JsonElement payload = doc.RootElement;
            string? cwd = GetString(payload, "cwd");
            instance = HookPaths.ResolveInstance(cwd, projectDir);
            sessionId = GetString(payload, "session_id") ?? "";
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
            string ownLedger = HookLedgerPath(instance, sessionId);
            string sharedLedger = HookLedgerPath(instance, "");
            int pending = PendingCount(ownLedger) + (string.IsNullOrWhiteSpace(sessionId) ? 0 : PendingCount(sharedLedger));
            if (pending == 0) return "";

            string dbPath = HookPaths.DbPath(instance);
            if (!File.Exists(dbPath))
                return Warn(pending, ownLedger, "the instance has no store yet, so there is nothing to flush into");

            using SqliteConnection connection = HookStore.Open(dbPath);
            string result = new BrainFlushTool().ExecuteCli(connection, sessionId);
            return result.StartsWith("flushed ", StringComparison.Ordinal)
                ? $"grimora: {result}"
                : Warn(pending, ownLedger, result);
        }
        catch (Exception ex)
        {
            // Fail open (never block Stop), but never silently — a caught exception here (a locked
            // store, a malformed ledger line) is exactly the "something kept this from flushing" case
            // the durability guarantee exists for.
            return $"grimora WARNING: could not check for unflushed staged learnings ({ex.Message}).";
        }
    }

    /// <summary>BrainStageTool.LedgerPath only ever reads <see cref="SqliteConnection.DataSource"/> — it
    /// never opens the connection — so a throwaway, unopened one pointed at the instance's db path is
    /// enough to derive the ledger path without paying for a real open (the store may not even exist
    /// yet).</summary>
    private static string HookLedgerPath(string instance, string sessionId) =>
        BrainStageTool.LedgerPath(new SqliteConnection($"Data Source={HookPaths.DbPath(instance)}"), sessionId);

    private static int PendingCount(string ledger) =>
        File.Exists(ledger) ? File.ReadAllLines(ledger).Count(l => l.Trim().Length > 0) : 0;

    private static string Warn(int count, string ledger, string reason) =>
        $"grimora WARNING: {count} staged learning(s) in {ledger} were not flushed ({reason}). " +
        "Run `grimora flush` (or the brain_flush MCP tool) before they are lost.";
}
