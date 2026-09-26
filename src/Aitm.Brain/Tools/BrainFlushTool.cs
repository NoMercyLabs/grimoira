using System.Text;
using System.Text.Json;
using Aitm.Brain.Data;
using Aitm.Store.Tools;
using Microsoft.Data.Sqlite;

namespace Aitm.Brain.Tools;

/// <summary>
/// BRAIN/write-side brake, "flush" half: commits every line <see cref="BrainStageTool"/> appended to the
/// ledger. CLI verb <c>flush</c> and MCP tool <c>brain_flush</c> are one job with two shapes today
/// (RESTRUCTURE.md section 2.2). <c>ExecuteCli</c> is copied verbatim from aitm.cs's <c>FlushCmd</c>
/// (aitm.cs:1954): one transaction, unconditional ledger delete, no retry. <c>ExecuteMcp</c> is copied
/// verbatim from mcp.cs's <c>brain_flush</c> (mcp.cs:967), including fix 668134a: a line rejected on a
/// transient lock/busy error is kept in the ledger for the next flush to retry, instead of being dropped
/// with the rest of the ledger — the bug that lost two learnings under concurrent MCP writers. Permanent
/// rejects (supersession no-op, unknown kind, dangling ref) are reported and dropped; retrying them would
/// loop forever. Re-uses <see cref="BrainLearnTool"/> for each MCP line, exactly as mcp.cs's brain_flush
/// re-uses brain_learn.
/// </summary>
public sealed class BrainFlushTool : ITool
{
    private readonly BrainLearnTool _learn = new();

    public string Name => "flush";
    public string CliVerb => "flush";
    public string? McpName => "brain_flush";
    public string Help =>
        "flush : commit every staged learning (`aitm stage …`) into the brain in one transaction, then " +
        "clear the ledger. MCP brain_flush(): same job; a line rejected on a transient DB lock is kept " +
        "in the ledger for the next flush to retry instead of being dropped.";

    public string ExecuteCli(SqliteConnection connection)
    {
        string ledger = BrainStageTool.LedgerPath(connection);
        // See LedgerFileLock's own comment: a second `aitm stage`/`aitm flush` process (or an MCP call)
        // targeting this same ledger must not interleave with this read-modify-write — held for the whole
        // read/commit/delete so a stage landing between the read and the delete is never wiped by it.
        using IDisposable ledgerLock = LedgerFileLock.Acquire(ledger);
        if (!File.Exists(ledger)) return "nothing staged.";
        string[] lines = [.. File.ReadAllLines(ledger).Where(l => l.Trim().Length > 0)];
        if (lines.Length == 0) { File.Delete(ledger); return "nothing staged."; }
        int n = 0;
        BeginTransaction(connection);
        foreach (string line in lines)
        {
            using JsonDocument d = JsonDocument.Parse(line);
            JsonElement e = d.RootElement;
            switch (JStr(e, "k"))
            {
                case "node": BrainWriters.AddNode(connection, JStr(e, "key"), JStr(e, "kind"), JStr(e, "label"), JStr(e, "gloss"), JStr(e, "scheme"), JBool(e, "hard"), "flush"); n++; break;
                case "triple": BrainWriters.AddTriple(connection, JStr(e, "s"), JStr(e, "p"), JStr(e, "o"), JStr(e, "because"), "manual", JBool(e, "hard"), "flush"); n++; break;
                case "slot": BrainWriters.AddSlot(connection, JStr(e, "frame"), JStr(e, "name"), JStr(e, "value"), JStr(e, "facet"), JBool(e, "multi"), JStr(e, "because"), "manual", "flush"); n++; break;
            }
        }
        CommitTransaction(connection);
        File.Delete(ledger);
        BrainGapResolver.Resolve(connection, string.Join(' ', lines));
        return $"flushed {n} learning(s) into the brain.";
    }

    public string ExecuteMcp(SqliteConnection connection)
    {
        string ledger = BrainStageTool.LedgerPath(connection);
        // See LedgerFileLock's own comment: a pipelined brain_stage/brain_flush (or a second concurrent
        // brain_flush from another process entirely) targeting this same ledger must not interleave with
        // this read-modify-write.
        using (LedgerFileLock.Acquire(ledger))
        {
            if (!File.Exists(ledger)) return "nothing staged.";
            string[] lines = [.. File.ReadAllLines(ledger).Where(l => l.Trim().Length > 0)];
            if (lines.Length == 0) { File.Delete(ledger); return "nothing staged."; }
            int n = 0;
            StringBuilder rejects = new();
            // Lines that failed on a TRANSIENT lock are kept in the ledger so the next flush retries them —
            // a locked DB under concurrent MCP writers must never silently drop a staged learning (fix
            // 668134a: two learnings lost when the whole ledger was deleted on a "database is locked" reject).
            List<string> keepForRetry = [];
            foreach (string line in lines)
            {
                using JsonDocument d = JsonDocument.Parse(line);
                JsonElement e = d.RootElement;
                string res = JStr(e, "k") switch
                {
                    "node" => _learn.ExecuteMcp(connection, "node", JStr(e, "key"), JStr(e, "kind"), JStr(e, "label"), JStr(e, "gloss"), "", JBool(e, "hard")),
                    "triple" => _learn.ExecuteMcp(connection, "triple", JStr(e, "s"), JStr(e, "p"), JStr(e, "o"), "", JStr(e, "because"), JBool(e, "hard")),
                    "slot" => _learn.ExecuteMcp(connection, "slot", JStr(e, "frame"), JStr(e, "name"), JStr(e, "value"), "", JStr(e, "because"), false),
                    _ => "skip (unknown kind).",
                };
                bool rejected = res.StartsWith("rejected", StringComparison.Ordinal) || res.StartsWith("unknown", StringComparison.Ordinal) || res.StartsWith("skip", StringComparison.Ordinal);
                if (rejected)
                {
                    rejects.AppendLine($"  ! {line} -> {res}");
                    if (res.Contains("locked", StringComparison.OrdinalIgnoreCase) || res.Contains("busy", StringComparison.OrdinalIgnoreCase))
                        keepForRetry.Add(line);
                }
                else
                    n++;
            }
            if (keepForRetry.Count > 0) AtomicWriteAllLines(ledger, keepForRetry);
            else File.Delete(ledger);
            string retryNote = keepForRetry.Count > 0 ? $" {keepForRetry.Count} kept for retry (DB was locked — flush again)." : "";
            return $"flushed {n} learning(s) into the brain.{retryNote}" + (rejects.Length > 0 ? "\n" + rejects : "");
        }
    }

    /// <summary>Rewrites the ledger with exactly <paramref name="lines"/>, never leaving a reader (another
    /// process's <c>list</c>, or this same file re-opened after a crash mid-write) able to observe a
    /// half-written file: write the new content to a temp file beside the ledger, then rename it over the
    /// ledger in one filesystem operation. <see cref="File.Move(string, string, bool)"/> is atomic on both
    /// Windows (a rename within the same volume) and Linux (POSIX <c>rename(2)</c>) as long as the temp
    /// file is on the same volume, which it is here (same directory).</summary>
    private static void AtomicWriteAllLines(string path, IReadOnlyList<string> lines)
    {
        string tempPath = path + $".tmp-{Guid.NewGuid():N}";
        File.WriteAllLines(tempPath, lines);
        File.Move(tempPath, path, overwrite: true);
    }

    private static string JStr(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static bool JBool(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.True;

    private static void BeginTransaction(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "BEGIN";
        command.ExecuteNonQuery();
    }

    private static void CommitTransaction(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "COMMIT";
        command.ExecuteNonQuery();
    }
}
