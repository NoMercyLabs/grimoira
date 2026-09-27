using Aitm.Brain.Data;
using Aitm.Store.Data;
using Aitm.Store.Tools;
using Microsoft.Data.Sqlite;

namespace Aitm.Brain.Tools;

/// <summary>
/// Free-text recall — DB-side alias expansion -> porter FTS5 seed (live-filtered, recency/usage weighted)
/// -> same-statement traversal to who-uses-it + ref-pulled fact/rule, with a substring fallback when FTS
/// misses. CLI sub-verb <c>brain recall</c> and MCP tool <c>brain_recall</c> are one job with two shapes
/// today (RESTRUCTURE.md section 2.2), so this tool carries both, each copied verbatim from its own
/// oracle: <c>ExecuteCli</c> from aitm.cs's <c>BrainRecall</c> (aitm.cs:1512), <c>ExecuteMcp</c> from
/// mcp.cs's <c>brain_recall</c> (mcp.cs:730). The CLI side keeps 15 seed rows and 8 fallback rows
/// unclipped; the MCP side keeps 8 seed rows, clips <c>used_by</c>/<c>rule</c>/<c>fact</c> in SQL, and
/// budgets the whole output — kept as separate paths rather than unified, the same way <c>QueryTool</c>
/// keeps its CLI and MCP shapes apart.
/// </summary>
public sealed class BrainRecallTool : ITool
{
    // Same stopword list as aitm.cs's Tokens()/stop (aitm.cs:51, aitm.cs:743).
    private static readonly HashSet<string> CliStop = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "is", "a", "an", "of", "to", "in", "on", "for", "and", "or", "what", "how", "are", "does",
        "do", "it", "its", "be", "this", "that", "with", "as", "at", "by", "my", "i", "you", "we", "there",
        "was", "were", "which", "when", "where", "name", "called", "get", "got", "me", "us", "about",
    };

    // Same stopword list as mcp.cs's Tokens()/Stop (mcp.cs:93, mcp.cs:249).
    private static readonly HashSet<string> McpStop = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "is", "a", "an", "of", "to", "in", "on", "for", "and", "or", "what", "how", "are",
        "does", "do", "it", "its", "be", "this", "that", "with", "as", "at", "by", "my", "i", "you",
        "we", "there", "was", "were", "which", "when", "where", "name", "called", "get", "got",
    };

    public string Name => "brain recall";
    public string CliVerb => "brain recall";
    public string McpName => "brain_recall";
    public string Help =>
        "brain recall <free text>              natural-language question -> synonym expansion -> FTS " +
        "seed -> who-uses-it + governing rule + ground-truth fact. The catch-all when the question isn't " +
        "clearly scope/common/place.";

    public string ExecuteCli(SqliteConnection connection, string text)
    {
        if (text.Trim().Length == 0) return "usage: brain recall <free text>";
        List<string> toks = CliTokens(text);
        if (toks.Count == 0) return "  (no usable terms)";

        string rawList = string.Join(",", toks.Select((_, i) => $"($q{i})"));
        using SqliteCommand c = connection.CreateCommand();
        c.CommandText = $"""
            WITH q(raw) AS (VALUES {rawList}),
                expanded AS (
                  SELECT raw AS term FROM q
                  UNION SELECT a.canonical FROM q JOIN term_alias a ON a.term = q.raw),
                match_expr AS (SELECT group_concat('"'||term||'"',' OR ') AS m FROM expanded),
                seed AS (
                  SELECT n.id, n.k,
                    bm25(node_fts)
                      - 0.15 * MIN(COALESCE(u.hits, 0), 20)
                      - CASE WHEN u.last_used IS NULL THEN 0
                             WHEN julianday('now') - julianday(u.last_used) < 1 THEN 0.6
                             WHEN julianday('now') - julianday(u.last_used) < 7 THEN 0.3
                             WHEN julianday('now') - julianday(u.last_used) < 30 THEN 0.1
                             ELSE 0 END AS r
                  FROM node_fts f
                  JOIN node_now n ON n.id = f.rowid
                  LEFT JOIN usage u ON u.node_k = n.k
                  WHERE node_fts MATCH (SELECT m FROM match_expr)
                  ORDER BY r LIMIT 15)
                SELECT n.k, n.kind, n.label, n.gloss,
                       (SELECT group_concat(DISTINCT t.s) FROM triple_now t
                        WHERE t.o = n.k AND t.p IN ('consumes','governed_by','exposes','implements')) AS used_by,
                       (SELECT m.body  FROM ref r JOIN memory m ON m.k = r.payload_k
                        WHERE r.node_k = n.k AND r.channel='memory' LIMIT 1) AS rule,
                       (SELECT fa.value FROM ref r JOIN facts fa ON fa.k = r.payload_k
                        WHERE r.node_k = n.k AND r.channel='facts' LIMIT 1) AS fact
                FROM seed JOIN node_now n ON n.id = seed.id
                ORDER BY seed.r
            """;
        for (int i = 0; i < toks.Count; i++) c.Parameters.AddWithValue($"$q{i}", toks[i]);
        (string output, List<string> keys) = BrainCliRows.RunReader(c, announceEmpty: false);
        BrainUsage.Reinforce(connection, keys);
        if (keys.Count > 0) return output;

        // FTS + synonyms missed — substring fallback over label/gloss so a real query rarely comes up empty.
        using SqliteCommand fb = connection.CreateCommand();
        string likeClauses = string.Join(" OR ", toks.Select((_, i) => $"label LIKE $l{i} OR gloss LIKE $l{i}"));
        fb.CommandText = $"SELECT k, kind, label, gloss FROM node_now WHERE {likeClauses} LIMIT 8";
        for (int i = 0; i < toks.Count; i++) fb.Parameters.AddWithValue($"$l{i}", "%" + toks[i] + "%");
        string fallbackHeader = "  (no FTS match — substring fallback)\n";
        (string fbOutput, List<string> fbKeys) = BrainCliRows.RunReader(fb);
        BrainUsage.Reinforce(connection, fbKeys);
        if (fbKeys.Count == 0) GapLog.Record(connection, "brain_recall", string.Join(' ', toks));
        return fallbackHeader + fbOutput;
    }

    public string ExecuteMcp(SqliteConnection connection, string query)
    {
        List<string> toks = McpTokens(query);
        if (toks.Count == 0) return "no usable query terms.";
        string rawList = string.Join(",", toks.Select((_, i) => $"($q{i})"));
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            WITH q(raw) AS (VALUES {rawList}),
                expanded AS (SELECT raw AS term FROM q UNION SELECT a.canonical FROM q JOIN term_alias a ON a.term = q.raw),
                match_expr AS (SELECT group_concat('"'||term||'"',' OR ') AS m FROM expanded),
                seed AS (SELECT n.id, n.k,
                           bm25(node_fts) - 0.15*MIN(COALESCE(u.hits,0),20)
                           - CASE WHEN u.last_used IS NULL THEN 0
                                  WHEN julianday('now')-julianday(u.last_used) < 1 THEN 0.6
                                  WHEN julianday('now')-julianday(u.last_used) < 7 THEN 0.3
                                  WHEN julianday('now')-julianday(u.last_used) < 30 THEN 0.1
                                  ELSE 0 END AS r
                         FROM node_fts f JOIN node_now n ON n.id = f.rowid
                         LEFT JOIN usage u ON u.node_k = n.k
                         WHERE node_fts MATCH (SELECT m FROM match_expr) ORDER BY r LIMIT 8)
                SELECT n.k, n.label, n.gloss,
                       substr((SELECT group_concat(DISTINCT t.s) FROM triple_now t WHERE t.o = n.k AND t.p IN ('consumes','governed_by','exposes','implements')),1,100) AS used_by,
                       substr((SELECT m.body FROM ref r JOIN memory m ON m.k = r.payload_k WHERE r.node_k = n.k AND r.channel='memory' LIMIT 1),1,220) AS rule,
                       substr((SELECT fa.value FROM ref r JOIN facts fa ON fa.k = r.payload_k WHERE r.node_k = n.k AND r.channel='facts' LIMIT 1),1,220) AS fact
                FROM seed JOIN node_now n ON n.id = seed.id ORDER BY seed.r
            """;
        for (int i = 0; i < toks.Count; i++) cmd.Parameters.AddWithValue($"$q{i}", toks[i]);
        string res = BrainMcpRows.RowsK(connection, cmd);
        if (res != "(nothing)") return res;

        // FTS + synonyms missed — substring fallback over label/gloss
        using SqliteCommand fb = connection.CreateCommand();
        string likeClauses = string.Join(" OR ", toks.Select((_, i) => $"label LIKE $l{i} OR gloss LIKE $l{i}"));
        fb.CommandText = $"SELECT k, label, gloss FROM node_now WHERE {likeClauses} LIMIT 8";
        for (int i = 0; i < toks.Count; i++) fb.Parameters.AddWithValue($"$l{i}", "%" + toks[i] + "%");
        string fbRes = BrainMcpRows.RowsK(connection, fb);
        return fbRes == "(nothing)" ? "(nothing)" + McpLogGap(connection, query) : "(substring fallback)\n" + fbRes;
    }

    // Mirrors mcp.cs's LogGap (mcp.cs:139): the actual insert is GapLog.Record (Store); this only
    // reproduces the caller-facing suffix, including the "swallow on failure" behaviour.
    private static string McpLogGap(SqliteConnection connection, string query)
    {
        try
        {
            string norm = string.Join(' ', McpTokens(query));
            if (norm.Length < 3) return "";
            GapLog.Record(connection, "brain_recall", norm);
            return " [gap logged — stage the answer via brain_stage once you learn it]";
        }
        catch (SqliteException)
        {
            return "";
        }
    }

    private static List<string> CliTokens(string terms) =>
        [.. terms.ToLowerInvariant()
            .Split(" \t\r\n-_./\\,;:()[]{}<>\"'`|!?*+=&#@~%$^".ToCharArray(), StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.All(char.IsLetterOrDigit) && t.Length > 1 && !CliStop.Contains(t))
            .Distinct()];

    private static List<string> McpTokens(string terms) =>
        [.. terms.ToLowerInvariant()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(t => new string([.. t.Where(char.IsLetterOrDigit)]))
            .Where(t => t.Length > 1 && !McpStop.Contains(t))];
}
