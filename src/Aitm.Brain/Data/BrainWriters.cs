using System.Globalization;
using Aitm.Store.Data;
using Microsoft.Data.Sqlite;

namespace Aitm.Brain.Data;

/// <summary>
/// The write primitives every brain write-path shares (single learn, learn-batch, set-hard today; flush
/// and seed once they move). Supersede, never delete; the <c>node_*</c>/<c>slot_*</c> triggers keep FTS
/// synced and the triple triggers reject typo'd predicates/dangling refs. Copied verbatim from aitm.cs's
/// <c>AddNode</c>/<c>AddTriple</c>/<c>AddSlot</c> (aitm.cs:1611, 1629, 1655), with <see cref="MutationLog"/>
/// (Store) replacing the old inline <c>LogMutation</c> — the same log every mutating tool already writes
/// through. mcp.cs's inline node/triple/slot writers in its own <c>LearnCore</c> (mcp.cs:790) never called
/// a mutation log at all; using this shared writer for <c>brain_learn</c> too closes that gap without
/// changing brain_learn's returned text (RESTRUCTURE.md design checklist: every write is traced).
/// The caller owns the transaction.
/// </summary>
public static class BrainWriters
{
    public enum TripleWrite { UnknownPredicate, Reinforced, Inserted }

    /// <summary>Upsert a node: false (no write) if unchanged, else supersede the live row and return true.</summary>
    public static bool AddNode(SqliteConnection connection, string k, string kind, string label, string gloss, string scheme, bool hard, string why)
    {
        string sig = $"{kind}|{label}|{gloss}|{scheme}|{(hard ? 1 : 0)}";
        string? before = ScalarText(connection, "SELECT kind||'|'||label||'|'||gloss||'|'||COALESCE(scheme,'')||'|'||hard FROM node_now WHERE k=$k", ("$k", k));
        if (before == sig) return false;

        if (before is not null)
            Exec(connection, "UPDATE node SET valid_to=$now WHERE k=$k AND valid_to IS NULL", ("$now", Now()), ("$k", k));

        Exec(connection, "INSERT INTO node(k,kind,label,gloss,scheme,hard) VALUES($k,$ki,$l,$g,$s,$h)",
            ("$k", k), ("$ki", kind), ("$l", label), ("$g", gloss), ("$s", scheme.Length == 0 ? null : scheme), ("$h", hard ? 1 : 0));

        if (before is not null)
        {
            long newId = ScalarLong(connection, "SELECT id FROM node_now WHERE k=$k", ("$k", k));
            Exec(connection, "UPDATE node SET superseded_by=$nid WHERE k=$k AND valid_to IS NOT NULL AND superseded_by IS NULL", ("$nid", newId), ("$k", k));
        }

        MutationLog.Append(connection, "node", k, before is null ? "insert" : "update", before, sig, why);
        return true;
    }

    /// <summary>Insert a triple idempotently. Reasserting a known relation strengthens its confidence
    /// instead of duplicating it. <c>o_is_literal</c> is derived from <c>pred_vocab.is_link</c>.</summary>
    /// <param name="stderr">Where the unknown-predicate/contradiction warnings go. Defaults to
    /// <see cref="Console.Error"/> (RESTRUCTURE.md slice 29a) so callers that have not yet been threaded
    /// to a request-scoped writer — brain learn, flush and distill today — keep today's console output;
    /// a caller that owns one (brain learn-batch, spine-import) passes it explicitly.</param>
    public static TripleWrite AddTriple(SqliteConnection connection, string s, string p, string o, string because, string src, bool hard, string why, TextWriter? stderr = null)
    {
        TextWriter target = stderr ?? Console.Error;
        if (ScalarLong(connection, "SELECT count(*) FROM pred_vocab WHERE p=$p", ("$p", p)) == 0)
        {
            target.WriteLine($"unknown predicate '{p}' — add it to pred_vocab first.");
            return TripleWrite.UnknownPredicate;
        }

        if (ScalarLong(connection, "SELECT count(*) FROM triple_now WHERE s=$s AND p=$p AND o=$o", ("$s", s), ("$p", p), ("$o", o)) > 0)
        {
            Exec(connection, "UPDATE triple SET conf=MIN(conf+0.2,3.0) WHERE s=$s AND p=$p AND o=$o AND valid_to IS NULL", ("$s", s), ("$p", p), ("$o", o));
            return TripleWrite.Reinforced;
        }

        if (ScalarLong(connection, """
                SELECT count(*) FROM triple_now t JOIN pred_vocab v ON v.p=t.p
                WHERE t.s=$s AND t.o=$o AND (v.conflicts=$p OR t.p=(SELECT conflicts FROM pred_vocab WHERE p=$p))
                """, ("$s", s), ("$o", o), ("$p", p)) > 0)
            target.WriteLine($"warning: '{s} {p} {o}' contradicts an existing edge on the same pair — review.");

        long isLink = ScalarLong(connection, "SELECT is_link FROM pred_vocab WHERE p=$p", ("$p", p));
        Exec(connection, "INSERT INTO triple(s,p,o,o_is_literal,because,src,hard) VALUES($s,$p,$o,$lit,$b,$src,$h)",
            ("$s", s), ("$p", p), ("$o", o), ("$lit", 1 - isLink), ("$b", because), ("$src", src), ("$h", hard ? 1 : 0));
        MutationLog.Append(connection, "triple", $"{s} {p} {o}", "insert", null, $"{s}|{p}|{o}", why);
        return TripleWrite.Inserted;
    }

    /// <summary>Upsert a slot. <c>multi=false</c> is single-valued (supersede on change); <c>multi=true</c>
    /// is set-valued (add unless the exact value is already live).</summary>
    public static bool AddSlot(SqliteConnection connection, string frame, string name, string value, string facet, bool multi, string because, string src, string why)
    {
        if (multi)
        {
            if (ScalarLong(connection, "SELECT count(*) FROM slot_now WHERE frame_k=$f AND name=$n AND value=$v", ("$f", frame), ("$n", name), ("$v", value)) > 0)
                return false;
            Exec(connection, "INSERT INTO slot(frame_k,name,value,facet,multi,because,src) VALUES($f,$n,$v,$fa,1,$b,$src)",
                ("$f", frame), ("$n", name), ("$v", value), ("$fa", facet), ("$b", because), ("$src", src));
            MutationLog.Append(connection, "slot", $"{frame}/{name}={value}", "insert", null, value, why);
            return true;
        }

        string? before = ScalarText(connection, "SELECT value FROM slot_now WHERE frame_k=$f AND name=$n AND multi=0", ("$f", frame), ("$n", name));
        if (before == value) return false;

        if (before is not null)
            Exec(connection, "UPDATE slot SET valid_to=$now WHERE frame_k=$f AND name=$n AND multi=0 AND valid_to IS NULL", ("$now", Now()), ("$f", frame), ("$n", name));

        Exec(connection, "INSERT INTO slot(frame_k,name,value,facet,multi,because,src) VALUES($f,$n,$v,$fa,0,$b,$src)",
            ("$f", frame), ("$n", name), ("$v", value), ("$fa", facet), ("$b", because), ("$src", src));

        if (before is not null)
        {
            long nid = ScalarLong(connection, "SELECT id FROM slot_now WHERE frame_k=$f AND name=$n AND multi=0", ("$f", frame), ("$n", name));
            Exec(connection, "UPDATE slot SET superseded_by=$nid WHERE frame_k=$f AND name=$n AND valid_to IS NOT NULL AND superseded_by IS NULL", ("$nid", nid), ("$f", frame), ("$n", name));
        }

        MutationLog.Append(connection, "slot", $"{frame}/{name}", before is null ? "insert" : "update", before, value, why);
        return true;
    }

    private static string Now() => DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);

    private static void Exec(SqliteConnection connection, string sql, params (string name, object? value)[] parameters)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        foreach ((string name, object? value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    private static string? ScalarText(SqliteConnection connection, string sql, params (string name, object? value)[] parameters)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        foreach ((string name, object? value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command.ExecuteScalar() as string;
    }

    private static long ScalarLong(SqliteConnection connection, string sql, params (string name, object? value)[] parameters)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        foreach ((string name, object? value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return (long)(command.ExecuteScalar() ?? 0L);
    }
}
