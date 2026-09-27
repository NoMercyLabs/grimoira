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
///
/// Since <see cref="BrainStageTool.LedgerPath"/> keys a ledger by session, Stop only ever flushes two
/// files: this session's own (from the hook payload's <c>session_id</c>) and the legacy shared ledger any
/// session-unaware caller (a bare CLI stage with no <c>--session</c>) still writes to — never another
/// still-running session's own ledger, which is exactly the isolation the session keying exists for.
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
            string? cwd = payload.TryGetProperty("cwd", out JsonElement cwdEl) && cwdEl.ValueKind == JsonValueKind.String
                ? cwdEl.GetString()
                : null;
            instance = HookPaths.ResolveInstance(cwd, projectDir);
            sessionId = payload.TryGetProperty("session_id", out JsonElement sidEl) && sidEl.ValueKind == JsonValueKind.String
                ? sidEl.GetString() ?? ""
                : "";
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
            string dbPath = HookPaths.DbPath(instance);

            // Two ledgers to account for: this session's own (BrainStageTool.LedgerPath keys it by
            // session_id, same as MCP's brain_stage/brain_flush), and the legacy shared one any caller
            // with no session context (an older bridge, a bare `grimora stage` with no --session) still
            // writes to. Stop only knows its own session, so it can only ever flush its own ledger plus
            // that shared one — never another session's still-open ledger, which is exactly the isolation
            // brain_flush's own session keying now guarantees.
            string ownLedger = Grimora.Brain.Tools.BrainStageTool.LedgerPath(FakeConnectionFor(dbPath), sessionId);
            string sharedLedger = Grimora.Brain.Tools.BrainStageTool.LedgerPath(FakeConnectionFor(dbPath));

            (int pendingOwn, string[] ownLines) = ReadPending(ownLedger);
            (int pendingShared, string[] sharedLines) = string.IsNullOrWhiteSpace(sessionId) ? (0, []) : ReadPending(sharedLedger);
            int pending = pendingOwn + pendingShared;
            if (pending == 0) return "";

            if (!File.Exists(dbPath))
                return Warn(pending, ownLedger, "the instance has no store yet, so there is nothing to flush into");

            using SqliteConnection connection = HookStore.Open(dbPath);
            List<string> messages = [];
            List<string> warnings = [];
            if (ownLines.Length > 0) Flush(connection, sessionId, ownLedger, messages, warnings);
            if (sharedLines.Length > 0) Flush(connection, "", sharedLedger, messages, warnings);

            return warnings.Count > 0
                ? string.Join('\n', warnings)
                : $"grimora: {string.Join(' ', messages)}";
        }
        catch (Exception ex)
        {
            // Fail open (never block Stop), but never silently — a caught exception here (a locked
            // store, a malformed ledger line) is exactly the "something kept this from flushing" case
            // the durability guarantee exists for.
            return $"grimora WARNING: could not check for unflushed staged learnings ({ex.Message}).";
        }
    }

    private static (int count, string[] lines) ReadPending(string ledger)
    {
        if (!File.Exists(ledger)) return (0, []);
        string[] lines = [.. File.ReadAllLines(ledger).Where(l => l.Trim().Length > 0)];
        return (lines.Length, lines);
    }

    private static void Flush(SqliteConnection connection, string sessionId, string ledger, List<string> messages, List<string> warnings)
    {
        string result = new BrainFlushTool().ExecuteCli(connection, sessionId);
        if (result.StartsWith("flushed ", StringComparison.Ordinal)) messages.Add(result);
        else warnings.Add(Warn(1, ledger, result));
    }

    /// <summary>BrainStageTool.LedgerPath only ever reads <see cref="SqliteConnection.DataSource"/> — it
    /// never opens the connection — so a throwaway, unopened one pointed at the real db path is enough to
    /// derive the ledger path without paying for a real open this method may not need (the ledger might
    /// not even exist).</summary>
    private static SqliteConnection FakeConnectionFor(string dbPath) => new($"Data Source={dbPath}");

    private static string Warn(int count, string ledger, string reason) =>
        $"grimora WARNING: {count} staged learning(s) in {ledger} were not flushed ({reason}). " +
        "Run `grimora flush` (or the brain_flush MCP tool) before they are lost.";
}
