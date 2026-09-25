using Aitm.Brain.Data;
using Aitm.Store.Tools;
using Microsoft.Data.Sqlite;

namespace Aitm.Brain.Tools;

/// <summary>
/// Placement — where new code of a kind goes AND how it's written here, inheriting project/layer
/// conventions up the broader chain (nearest frame wins), plus belongs_in and forbidden patterns. One
/// recursive query. CLI sub-verb <c>brain place</c> and MCP tool <c>brain_place</c> are one job with two
/// shapes today (RESTRUCTURE.md section 2.2), so this tool carries both, each copied verbatim from its
/// own oracle: <c>ExecuteCli</c> from aitm.cs's <c>BrainPlace</c> (aitm.cs:1481), <c>ExecuteMcp</c> from
/// mcp.cs's <c>brain_place</c> (mcp.cs:707). The CLI side selects 4 columns including <c>facet</c> and
/// never logs a gap; the MCP side selects 3 and logs a gap on an empty result — kept as separate paths
/// rather than unified, the same way <c>ImpactTool</c> keeps its CLI and MCP shapes apart.
/// </summary>
public sealed class BrainPlaceTool : ITool
{
    // Same stopword list as mcp.cs's Tokens()/Stop (mcp.cs:93, mcp.cs:249) — the gap query is normalised
    // the same way before it is logged.
    private static readonly HashSet<string> McpStop = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "is", "a", "an", "of", "to", "in", "on", "for", "and", "or", "what", "how", "are",
        "does", "do", "it", "its", "be", "this", "that", "with", "as", "at", "by", "my", "i", "you",
        "we", "there", "was", "were", "which", "when", "where", "name", "called", "get", "got",
    };

    public string Name => "brain place";
    public string CliVerb => "brain place";
    public string? McpName => "brain_place";
    public string Help =>
        "brain place <codekind>                where new code of a kind belongs AND how it's written " +
        "here, inheriting conventions up the broader chain. Call BEFORE writing new code.";

    public string ExecuteCli(SqliteConnection connection, string codekind)
    {
        if (codekind.Length == 0) return "usage: brain place <codekind>";
        string kind = NormalizeKindCli(codekind);
        using SqliteCommand c = connection.CreateCommand();
        c.CommandText = """
            WITH RECURSIVE chain(k,depth) AS (
              SELECT $kind, 0
              UNION ALL
              SELECT t.o, c.depth+1 FROM triple_now t JOIN chain c ON t.s = c.k
              WHERE t.p = 'broader' AND c.depth < 6),
            ranked AS (
              SELECT s.name, s.value, s.facet, s.because,
                     ROW_NUMBER() OVER (PARTITION BY s.name ORDER BY c.depth, s.id) AS rn
              FROM chain c JOIN slot_now s ON s.frame_k = c.k)
            SELECT name AS slot, value, facet, because FROM ranked WHERE rn = 1
            UNION ALL
            SELECT 'belongs_in', t.o, 'ref:node', n.gloss
            FROM triple_now t JOIN node_now n ON n.k = t.o
            WHERE t.s = $kind AND t.p = 'belongs_in'
            UNION ALL
            SELECT 'forbidden', t.o, 'ref:rule', n.gloss
            FROM triple_now t JOIN node_now n ON n.k = t.o
            WHERE t.s = $kind AND t.p = 'forbids'
            ORDER BY 1
            """;
        c.Parameters.AddWithValue("$kind", kind);
        (string output, _) = BrainCliRows.RunReader(c);
        BrainUsage.Reinforce(connection, [kind]);
        return output;
    }

    public string ExecuteMcp(SqliteConnection connection, string codekind)
    {
        string kind = codekind.Contains(':') ? codekind.Trim() : "kind:" + codekind.Trim();
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = """
            WITH RECURSIVE chain(k,depth) AS (
              SELECT $kind, 0 UNION ALL
              SELECT t.o, c.depth+1 FROM triple_now t JOIN chain c ON t.s = c.k WHERE t.p = 'broader' AND c.depth < 6),
            ranked AS (SELECT s.name, s.value, s.facet, s.because,
                   ROW_NUMBER() OVER (PARTITION BY s.name ORDER BY c.depth, s.id) AS rn
                   FROM chain c JOIN slot_now s ON s.frame_k = c.k)
            SELECT name AS slot, value, because FROM ranked WHERE rn = 1
            UNION ALL SELECT 'belongs_in', t.o, n.gloss FROM triple_now t JOIN node_now n ON n.k=t.o WHERE t.s=$kind AND t.p='belongs_in'
            UNION ALL SELECT 'forbidden', t.o, n.gloss FROM triple_now t JOIN node_now n ON n.k=t.o WHERE t.s=$kind AND t.p='forbids'
            ORDER BY 1
            """;
        cmd.Parameters.AddWithValue("$kind", kind);
        string placed = BrainMcpRows.Rows(cmd);
        BrainUsage.Reinforce(connection, [kind]);
        return placed == "(nothing)" ? placed + LogGap(connection, codekind) : placed;
    }

    private static string NormalizeKindCli(string input)
    {
        string t = input.Trim();
        return t.Contains(':') ? t : "kind:" + t;
    }

    private static List<string> McpTokens(string terms) =>
        terms.ToLowerInvariant()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(t => new string(t.Where(char.IsLetterOrDigit).ToArray()))
            .Where(t => t.Length > 1 && !McpStop.Contains(t))
            .ToList();

    // Copied verbatim from mcp.cs's LogGap (mcp.cs:139): every refused/empty lookup is recorded so the
    // store knows what it does NOT know, and the caller is told the gap was logged.
    private static string LogGap(SqliteConnection connection, string codekind)
    {
        try
        {
            string norm = string.Join(' ', McpTokens(codekind));
            if (norm.Length < 3) return "";
            using SqliteCommand g = connection.CreateCommand();
            g.CommandText = """
                INSERT INTO gaps(query,tool,misses,first_ts,last_ts)
                VALUES($q,$t,1,strftime('%Y-%m-%dT%H:%M:%fZ','now'),strftime('%Y-%m-%dT%H:%M:%fZ','now'))
                ON CONFLICT(query) DO UPDATE SET misses=misses+1, last_ts=strftime('%Y-%m-%dT%H:%M:%fZ','now'), status='open', tool=$t
                """;
            g.Parameters.AddWithValue("$q", norm);
            g.Parameters.AddWithValue("$t", "brain_place");
            g.ExecuteNonQuery();
            return " [gap logged — stage the answer via brain_stage once you learn it]";
        }
        catch (SqliteException)
        {
            return "";
        }
    }
}
