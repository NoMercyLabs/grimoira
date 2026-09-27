using Grimora.Store.Data;
using Grimora.Store.Tools;
using Microsoft.Data.Sqlite;

namespace Grimora.Graph.Tools;

/// <summary>
/// What a symbol IS (kind + where it's declared), who uses it (grouped by project, top sites), and any
/// docs/rules that mention it — the "explain this to me" answer graphify's <c>explain</c> gave. CLI verb
/// <c>graph-explain</c> and MCP tool <c>graph_explain</c> are one job with two shapes today (RESTRUCTURE.md
/// section 2.2): <c>ExecuteCli</c> copied verbatim from grimora.cs's <c>GraphExplainCmd</c> (grimora.cs:2733),
/// <c>ExecuteMcp</c> from mcp.cs's <c>graph_explain</c> (mcp.cs:1229) — kept as separate paths because the
/// two oracles diverge slightly (only the MCP side logs a gap on a miss), the same way <c>ImpactTool</c>
/// keeps its CLI and MCP shapes apart.
///
/// Known-issue fix (RESTRUCTURE.md slice 13): today's "used by" count in both oracles is
/// <c>COUNT(*) FROM edges WHERE symbol=$s AND project != ''</c>, which counts a symbol's own declaration
/// row (<c>contract='decl'</c>) as a "used by" site for its home project. A symbol declared once and
/// never consumed elsewhere in that project should not be reported as having a use there. This tool
/// excludes <c>contract='decl'</c> rows from the "used by" tally; the declaration itself is already
/// reported separately, above, as "defined:". The same fix is applied to today's grimora.cs
/// <c>GraphExplainCmd</c> and mcp.cs <c>graph_explain</c>, since it is small and safe and those are what
/// runs in production right now.
/// </summary>
public sealed class GraphExplainTool : ITool
{
    public string Name => "graph-explain";
    public string CliVerb => "graph-explain";
    public string McpName => "graph_explain";
    public string Help =>
        "graph-explain <symbol>                what a symbol is, who uses it, and docs/rules that mention it. " +
        "MCP graph_explain(symbol): same lookup, logs a gap on a miss.";

    public string ExecuteCli(SqliteConnection connection, string symbol)
    {
        if (symbol.Trim().Length == 0) return "usage: grimora graph-explain <symbol>";
        string? resolved = Resolve(connection, symbol);
        if (resolved == null) return $"no symbol matches \"{symbol}\".";
        return BuildExplanation(connection, resolved);
    }

    public string ExecuteMcp(SqliteConnection connection, string symbol)
    {
        if (symbol.Trim().Length == 0) return "provide a symbol name.";
        string? resolved = Resolve(connection, symbol);
        if (resolved == null) return $"no symbol matches \"{symbol}\".{McpLogGap(connection, symbol)}";
        return BuildExplanation(connection, resolved);
    }

    private static string? Resolve(SqliteConnection connection, string symbol)
    {
        using (SqliteCommand c = connection.CreateCommand())
        {
            c.CommandText = "SELECT symbol FROM edges WHERE symbol=$s COLLATE NOCASE LIMIT 1";
            c.Parameters.AddWithValue("$s", symbol);
            if (c.ExecuteScalar() is string exact) return exact;
        }
        using (SqliteCommand c = connection.CreateCommand())
        {
            c.CommandText = "SELECT symbol FROM edges WHERE symbol LIKE $s ORDER BY length(symbol) LIMIT 1";
            c.Parameters.AddWithValue("$s", "%" + symbol + "%");
            return c.ExecuteScalar() as string;
        }
    }

    private static string BuildExplanation(SqliteConnection connection, string resolved)
    {
        bool hasFileRel = Schema.GraphFileRelSchema.HasColumn(connection, "edges", "file_rel");
        Dictionary<string, string> roots = hasFileRel ? Schema.GraphFileRelSchema.LoadProjectRoots(connection) : [];
        string ResolveFile(string project, string file, bool isDbNull, Func<string> getFileRel) =>
            Schema.GraphFileRelSchema.ResolveFull(
                roots.GetValueOrDefault(project),
                hasFileRel && !isDbNull ? getFileRel() : null, file);

        List<string> lines = [];
        List<(string project, string file, int line, string usage)> declRows = [];
        using (SqliteCommand c = connection.CreateCommand())
        {
            c.CommandText = hasFileRel
                ? "SELECT project, file, line, usage, file_rel FROM edges WHERE symbol=$s AND contract='decl' ORDER BY project, file LIMIT 5"
                : "SELECT project, file, line, usage FROM edges WHERE symbol=$s AND contract='decl' ORDER BY project, file LIMIT 5";
            c.Parameters.AddWithValue("$s", resolved);
            using SqliteDataReader r = c.ExecuteReader();
            while (r.Read())
            {
                string project = r.GetString(0);
                string file = ResolveFile(project, r.GetString(1), !hasFileRel || r.IsDBNull(4), () => r.GetString(4));
                declRows.Add((project, file, r.GetInt32(2), r.GetString(3)));
            }
        }
        string kind = declRows.Count > 0 ? declRows[0].usage : (Scalar(connection, "SELECT contract FROM edges WHERE symbol=$s AND contract != '' LIMIT 1", resolved) ?? "unknown");
        lines.Add($"{resolved}  [{kind}]");
        if (declRows.Count == 0) lines.Add("  defined: (no indexed declaration — curated usage edge only, or not yet indexed)");
        foreach ((string project, string file, int line, string usage) d in declRows)
            lines.Add($"  defined: {d.project}  {d.file}:{d.line}");

        // Known-issue fix: contract != 'decl' excludes a symbol's own declaration row from its "used by"
        // count — a declaration is not a use, and is already reported above as "defined:".
        List<(string project, int sites, int files)> byProject = [];
        using (SqliteCommand c = connection.CreateCommand())
        {
            c.CommandText = @"SELECT project, COUNT(*), COUNT(DISTINCT file) FROM edges
                WHERE symbol=$s AND project IS NOT NULL AND project != '' AND contract != 'decl'
                GROUP BY project ORDER BY 2 DESC";
            c.Parameters.AddWithValue("$s", resolved);
            using SqliteDataReader r = c.ExecuteReader();
            while (r.Read()) byProject.Add((r.GetString(0), r.GetInt32(1), r.GetInt32(2)));
        }
        lines.Add($"used by ({byProject.Sum(p => p.sites)} site(s) across {byProject.Count} project(s)):");
        foreach ((string project, int sites, int files) p in byProject)
            lines.Add($"  {p.project,-14} {p.sites} site(s), {p.files} file(s)");

        lines.Add("top sites:");
        using (SqliteCommand c = connection.CreateCommand())
        {
            c.CommandText = hasFileRel
                ? @"SELECT project, file, line, usage, hardcoded, file_rel FROM edges WHERE symbol=$s
                    ORDER BY hardcoded DESC, project, file LIMIT 10"
                : @"SELECT project, file, line, usage, hardcoded FROM edges WHERE symbol=$s
                    ORDER BY hardcoded DESC, project, file LIMIT 10";
            c.Parameters.AddWithValue("$s", resolved);
            using SqliteDataReader r = c.ExecuteReader();
            int n = 0;
            while (r.Read())
            {
                n++;
                bool hard = r.GetInt32(4) == 1;
                string project = r.GetString(0);
                string file = ResolveFile(project, r.GetString(1), !hasFileRel || r.IsDBNull(5), () => r.GetString(5));
                lines.Add($"  {project,-10} {file}:{r.GetInt32(2)}{(hard ? " [HARDCODED]" : "")}  {Clip(r.GetString(3), 60)}");
            }
            if (n == 0) lines.Add("  (none recorded)");
        }

        lines.Add("uses: not tracked — the edges table records declaration/usage sites, not call relationships.");

        string match = string.Join(" OR ", Tokens(resolved).Select(t => $"\"{t}\""));
        if (match.Length > 0)
        {
            try
            {
                using SqliteCommand c = connection.CreateCommand();
                c.CommandText = @"SELECT title, path FROM (SELECT k, bm25(docs_fts) AS s FROM docs_fts WHERE docs_fts MATCH $m ORDER BY s LIMIT 2) x
                    JOIN docs d ON d.k=x.k ORDER BY x.s";
                c.Parameters.AddWithValue("$m", match);
                using SqliteDataReader r = c.ExecuteReader();
                while (r.Read()) lines.Add($"  doc: {Clip(r.GetString(0), 60)} ({Clip(r.GetString(1), 50)})");
            }
            catch (SqliteException) { /* docs not indexed */ }
            try
            {
                using SqliteCommand c = connection.CreateCommand();
                c.CommandText = @"SELECT hook FROM (SELECT k, bm25(memory_fts) AS s FROM memory_fts WHERE memory_fts MATCH $m ORDER BY s LIMIT 2) x
                    JOIN memory mem ON mem.k=x.k ORDER BY x.s";
                c.Parameters.AddWithValue("$m", match);
                using SqliteDataReader r = c.ExecuteReader();
                while (r.Read()) lines.Add($"  rule: {Clip(r.GetString(0), 70)}");
            }
            catch (SqliteException) { /* memory not indexed */ }
        }
        return CapLines(lines);
    }

    // Mirrors mcp.cs's LogGap (mcp.cs:139): the actual insert is GapLog.Record (Store); this only
    // reproduces the caller-facing suffix, including the "swallow on failure" behaviour.
    private static string McpLogGap(SqliteConnection connection, string query)
    {
        try
        {
            string norm = string.Join(' ', Tokens(query));
            if (norm.Length < 3) return "";
            GapLog.Record(connection, "graph_explain", norm);
            return " [gap logged — stage the answer via brain_stage once you learn it]";
        }
        catch (SqliteException)
        {
            return "";
        }
    }

    private static string? Scalar(SqliteConnection connection, string sql, string symbol)
    {
        using SqliteCommand c = connection.CreateCommand();
        c.CommandText = sql;
        c.Parameters.AddWithValue("$s", symbol);
        return c.ExecuteScalar() as string;
    }

    private static string CapLines(List<string> lines, int max = 40)
    {
        if (lines.Count <= max) return string.Join("\n", lines);
        return string.Join("\n", lines.Take(max)) + $"\n(+{lines.Count - max} more line(s) truncated — narrow the query)";
    }

    private static readonly HashSet<string> Stop = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "is", "a", "an", "of", "to", "in", "on", "for", "and", "or", "what", "how", "are", "does",
        "do", "it", "its", "be", "this", "that", "with", "as", "at", "by", "my", "i", "you", "we", "there",
        "was", "were", "which", "when", "where", "name", "called", "get", "got", "me", "us", "about",
    };

    private static List<string> Tokens(string terms) =>
        [.. terms.ToLowerInvariant()
            .Split(" \t\r\n-_./\\,;:()[]{}<>\"'`|!?*+=&#@~%$^".ToCharArray(), StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.All(char.IsLetterOrDigit) && t.Length > 1 && !Stop.Contains(t))
            .Distinct()];

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..max].TrimEnd() + "…";
}
