using System.Text;
using Grimora.Store.Data;
using Grimora.Store.Tools;
using Microsoft.Data.Sqlite;

namespace Grimora.Facts.Tools;

/// <summary>
/// Looks up a verified fact by free-text query. CLI verb <c>query</c> and MCP tool <c>fact</c> are one
/// job with two shapes today (RESTRUCTURE.md section 2.2), so this tool carries both, each copied
/// verbatim from its own oracle: <c>ExecuteCli</c> from grimora.cs's <c>QueryCmd</c>/<c>Search</c>
/// (grimora.cs:754, grimora.cs:784), <c>ExecuteMcp</c> from mcp.cs's <c>fact</c> (mcp.cs:260). The two oracles
/// tokenize, gate and format differently (the CLI keeps 5 hits and de-duplicates tokens, the MCP side
/// keeps 3, never de-duplicates, and reinforces the usage signal) — kept as separate paths rather than
/// unified, the same way <c>HistoryTool</c> keeps its CLI and MCP shapes apart.
/// </summary>
public sealed class QueryTool(IUsageSignal usageSignal) : ITool
{
    // Calibrated on real data (grimora.cs:58-64): on-target queries score <= -8.6, generic single-token
    // noise scores >= -2.2; -3.0 sits cleanly in the gap. Only trusted once the corpus is large enough
    // for IDF to discriminate (below that, return the best match rather than over-refuse a young store).
    private const double RelevanceFloor = -3.0;
    private const int FloorMinCorpus = 20;
    private const int CellCap = 160;

    private static readonly HashSet<string> CliStop = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "is", "a", "an", "of", "to", "in", "on", "for", "and", "or", "what", "how", "are", "does",
        "do", "it", "its", "be", "this", "that", "with", "as", "at", "by", "my", "i", "you", "we", "there",
        "was", "were", "which", "when", "where", "name", "called", "get", "got", "me", "us", "about",
    };

    private static readonly HashSet<string> McpStop = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "is", "a", "an", "of", "to", "in", "on", "for", "and", "or", "what", "how", "are",
        "does", "do", "it", "its", "be", "this", "that", "with", "as", "at", "by", "my", "i", "you",
        "we", "there", "was", "were", "which", "when", "where", "name", "called", "get", "got",
    };

    public string Name => "query";
    public string CliVerb => "query";
    public string McpName => "fact";
    public string Help =>
        "query <text>                          look up a verified fact (CLI: up to 5 hits). " +
        "MCP fact(query): up to 3 hits, reinforces the usage signal.";

    public string ExecuteCli(SqliteConnection connection, string terms)
    {
        (List<(string term, string category, string value, string source, double score)> rows, double ms) =
            Search(connection, terms, 5);
        if (rows.Count == 0)
        {
            GapLog.Record(connection, "query", string.Join(' ', CliTokens(terms)));
            return $"no confident answer for \"{terms}\" — not in the knowledge base (refusing rather than guessing; gap logged). ({ms:F2}ms)";
        }
        StringBuilder sb = new();
        foreach ((string term, string category, string value, string source, double score) in rows)
            sb.AppendLine($"• {term}  [{category}]  (score {score:F2})\n  {value}\n  source: {source}");
        sb.Append($"({ms:F2}ms)");
        return sb.ToString();
    }

    public string ExecuteMcp(SqliteConnection connection, string query)
    {
        List<string> qToks = McpTokens(query);
        if (qToks.Count == 0) return "no usable query terms.";
        string match = string.Join(" OR ", qToks.Select(t => $"\"{t}\""));
        bool gate = Scalar(connection, "SELECT count(*) FROM facts") >= FloorMinCorpus;

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT f.term,f.category,f.value,f.source,x.s,f.aliases,f.notes,f.k
            FROM (SELECT k, bm25(facts_fts) AS s FROM facts_fts WHERE facts_fts MATCH $m ORDER BY s LIMIT 3) x
            JOIN facts f ON f.k=x.k ORDER BY x.s
            """;
        command.Parameters.AddWithValue("$m", match);

        StringBuilder sb = new();
        List<string> hitKeys = [];
        using (SqliteDataReader reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                if (gate && reader.GetDouble(4) > RelevanceFloor) continue;
                string hay = $"{reader.GetString(0)} {reader.GetString(5)} {reader.GetString(1)} {reader.GetString(2)} {reader.GetString(6)}".ToLowerInvariant();
                int present = qToks.Count(t => hay.Contains(t, StringComparison.Ordinal));
                if (qToks.Count >= 3 && present <= 1) continue;
                hitKeys.Add(reader.GetString(7));
                sb.AppendLine($"• {reader.GetString(0)} [{reader.GetString(1)}]\n  {reader.GetString(2)}\n  source: {Clip(reader.GetString(3), CellCap)}");
            }
        }
        if (sb.Length == 0)
            return $"no confident answer for \"{query}\" — not in the knowledge base (refusing rather than guessing).{McpLogGap(connection, query)}";
        usageSignal.Reinforce(connection, "facts", hitKeys);
        return OutputBudget.Clip(sb.ToString());
    }

    // Mirrors mcp.cs's LogGap (mcp.cs:139): the actual insert is GapLog.Record (Store); this only
    // reproduces the caller-facing suffix, including the "swallow on failure" behaviour.
    private static string McpLogGap(SqliteConnection connection, string query)
    {
        try
        {
            string norm = string.Join(' ', McpTokens(query));
            if (norm.Length < 3) return "";
            GapLog.Record(connection, "fact", norm);
            return " [gap logged — stage the answer via brain_stage once you learn it]";
        }
        catch (SqliteException)
        {
            return "";
        }
    }

    // FTS5 MATCH isolated in a subquery so it actually filters (a JOIN+MATCH leaked all rows).
    // bm25 lower = better; the score is returned so callers can apply a relevance gate.
    private static (List<(string, string, string, string, double)> rows, double ms) Search(SqliteConnection connection, string terms, int limit)
    {
        List<(string, string, string, string, double)> rows = [];
        List<string> tokens = CliTokens(terms);
        string match = string.Join(" OR ", tokens.Select(t => $"\"{t}\""));
        if (match.Length == 0) return (rows, 0);
        bool gate = Scalar(connection, "SELECT count(*) FROM facts") >= FloorMinCorpus;
        System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT f.term,f.category,f.value,f.source,m.score,f.aliases,f.notes
            FROM (SELECT k, bm25(facts_fts) AS score FROM facts_fts WHERE facts_fts MATCH $m ORDER BY score LIMIT $l) m
            JOIN facts f ON f.k=m.k ORDER BY m.score
            """;
        command.Parameters.AddWithValue("$m", match);
        command.Parameters.AddWithValue("$l", limit);
        using (SqliteDataReader reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                double score = reader.GetDouble(4);
                if (gate && score > RelevanceFloor) continue;
                string hay = $"{reader.GetString(0)} {reader.GetString(5)} {reader.GetString(1)} {reader.GetString(2)} {reader.GetString(6)}".ToLowerInvariant();
                int present = tokens.Count(t => hay.Contains(t, StringComparison.Ordinal));
                if (tokens.Count >= 3 && present <= 1) continue;
                rows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), score));
            }
        }
        sw.Stop();
        return (rows, sw.Elapsed.TotalMilliseconds);
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

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..max].TrimEnd() + "…";

    private static long Scalar(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)(command.ExecuteScalar() ?? 0L);
    }
}
