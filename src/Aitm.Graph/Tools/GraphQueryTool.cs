using Aitm.Store.Data;
using Aitm.Store.Tools;
using Microsoft.Data.Sqlite;
using System.Text.RegularExpressions;

namespace Aitm.Graph.Tools;

/// <summary>
/// "Find the most relevant symbols, files and docs for a question" — the code channel's own scoring
/// (word-boundary symbol match, grouped by project) plus the same docs_fts lookup <c>doc</c> already
/// uses. CLI verb <c>graph-query</c> and MCP tool <c>graph_query</c> are one job with two shapes today
/// (RESTRUCTURE.md section 2.2): <c>ExecuteCli</c> copied verbatim from aitm.cs's <c>GraphQueryCmd</c>
/// (aitm.cs:2619), <c>ExecuteMcp</c> from mcp.cs's <c>graph_query</c> (mcp.cs:1116). The two oracles
/// tokenize differently (aitm.cs keeps punctuation-stripped alnum tokens and de-duplicates; mcp.cs
/// strips non-alnum characters per token and keeps duplicates) and only the MCP side logs a gap on a
/// miss — kept as separate paths rather than unified, the same way <c>ImpactTool</c> keeps its CLI and
/// MCP shapes apart.
/// </summary>
public sealed partial class GraphQueryTool : ITool
{
    public string Name => "graph-query";
    public string CliVerb => "graph-query";
    public string? McpName => "graph_query";
    public string Help =>
        "graph-query <question>                find the most relevant symbols/files/docs for a question. " +
        "MCP graph_query(question): same lookup, logs a gap on a miss.";

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

    public string ExecuteCli(SqliteConnection connection, string question)
    {
        List<string> toks = CliTokens(question);
        if (toks.Count == 0) return "usage: aitm graph-query <question>";

        List<string> lines = BuildLines(connection, toks, question, out _);
        AppendDocs(connection, lines, CliMatch(question));
        return CapLines(lines);
    }

    public string ExecuteMcp(SqliteConnection connection, string question)
    {
        List<string> toks = McpTokens(question);
        if (toks.Count == 0) return "no usable query terms.";

        List<string> lines = BuildLines(connection, toks, question, out bool matched);
        if (!matched) lines[0] += McpLogGap(connection, question);
        AppendDocs(connection, lines, McpMatch(question));
        return CapLines(lines);
    }

    private static List<string> BuildLines(SqliteConnection connection, List<string> toks, string question, out bool matched)
    {
        bool hasFileRel = Schema.GraphFileRelSchema.HasColumn(connection, "edges", "file_rel");
        Dictionary<string, string> roots = hasFileRel ? Schema.GraphFileRelSchema.LoadProjectRoots(connection) : [];

        HashSet<string> candidates = new(StringComparer.OrdinalIgnoreCase);
        foreach (string t in toks)
        {
            using SqliteCommand c = connection.CreateCommand();
            c.CommandText = "SELECT DISTINCT symbol FROM edges WHERE symbol LIKE $p LIMIT 400";
            c.Parameters.AddWithValue("$p", "%" + t + "%");
            using SqliteDataReader r = c.ExecuteReader();
            while (r.Read()) candidates.Add(r.GetString(0));
        }
        List<string> matchedSymbols = [.. candidates.Where(s => SymbolMatchesTokens(s, toks)).OrderBy(s => s, StringComparer.OrdinalIgnoreCase).Take(20)];

        List<string> lines = [];
        matched = matchedSymbols.Count > 0;
        if (matchedSymbols.Count == 0)
        {
            lines.Add($"no symbol matches \"{question}\".");
            return lines;
        }

        List<(string symbol, string project, string defLoc, List<(string project, int files)> byProject)> rows = [];
        foreach (string sym in matchedSymbols)
        {
            string? defLoc = null;
            string homeProject = "";
            using (SqliteCommand c = connection.CreateCommand())
            {
                c.CommandText = hasFileRel
                    ? "SELECT project, file, line, file_rel FROM edges WHERE symbol=$s AND contract='decl' LIMIT 1"
                    : "SELECT project, file, line FROM edges WHERE symbol=$s AND contract='decl' LIMIT 1";
                c.Parameters.AddWithValue("$s", sym);
                using SqliteDataReader r = c.ExecuteReader();
                if (r.Read())
                {
                    homeProject = r.GetString(0);
                    string? fileRel = hasFileRel && !r.IsDBNull(3) ? r.GetString(3) : null;
                    string file = Schema.GraphFileRelSchema.ResolveFull(
                        roots.TryGetValue(homeProject, out string? root) ? root : null, fileRel, r.GetString(1));
                    defLoc = $"{file}:{r.GetInt32(2)}";
                }
            }
            List<(string project, int files)> byProject = [];
            using (SqliteCommand c = connection.CreateCommand())
            {
                c.CommandText = @"SELECT project, COUNT(DISTINCT file) FROM edges
                    WHERE symbol=$s AND project IS NOT NULL AND project != ''
                    GROUP BY project ORDER BY 2 DESC LIMIT 5";
                c.Parameters.AddWithValue("$s", sym);
                using SqliteDataReader r = c.ExecuteReader();
                while (r.Read()) byProject.Add((r.GetString(0), r.GetInt32(1)));
            }
            if (homeProject.Length == 0) homeProject = byProject.FirstOrDefault().project ?? "(unknown)";
            rows.Add((sym, homeProject, defLoc ?? "(no indexed declaration)", byProject));
        }

        lines.Add($"{matchedSymbols.Count} symbol(s) matched across {rows.Select(r => r.project).Distinct().Count()} project(s):");
        foreach (IGrouping<string, (string symbol, string project, string defLoc, List<(string project, int files)> byProject)> g in rows.GroupBy(r => r.project).OrderByDescending(g => g.Count()))
        {
            lines.Add($"[{g.Key}]");
            foreach ((string symbol, string project, string defLoc, List<(string project, int files)> byProject) row in g)
            {
                lines.Add($"  {row.symbol}  defined: {row.defLoc}");
                if (row.byProject.Count > 0)
                    lines.Add("    used by: " + string.Join(", ", row.byProject.Select(p => $"{p.project} ({p.files} file(s))")));
            }
        }
        return lines;
    }

    private static void AppendDocs(SqliteConnection connection, List<string> lines, string match)
    {
        if (match.Length == 0) return;
        try
        {
            using SqliteCommand c = connection.CreateCommand();
            c.CommandText = @"SELECT d.title, d.path FROM (SELECT k, bm25(docs_fts) AS s FROM docs_fts WHERE docs_fts MATCH $m ORDER BY s LIMIT 3) x
                JOIN docs d ON d.k=x.k ORDER BY x.s";
            c.Parameters.AddWithValue("$m", match);
            using SqliteDataReader r = c.ExecuteReader();
            bool any = false;
            while (r.Read())
            {
                if (!any) { lines.Add("docs:"); any = true; }
                lines.Add($"  • {Clip(r.GetString(0), 70)} ({Clip(r.GetString(1), 60)})");
            }
        }
        catch (SqliteException) { /* docs not indexed for this instance */ }
    }

    // Mirrors mcp.cs's LogGap (mcp.cs:139): the actual insert is GapLog.Record (Store); this only
    // reproduces the caller-facing suffix, including the "swallow on failure" behaviour.
    private static string McpLogGap(SqliteConnection connection, string query)
    {
        try
        {
            string norm = string.Join(' ', McpTokens(query));
            if (norm.Length < 3) return "";
            GapLog.Record(connection, "graph_query", norm);
            return " [gap logged — stage the answer via brain_stage once you learn it]";
        }
        catch (SqliteException)
        {
            return "";
        }
    }

    private static bool SymbolMatchesTokens(string symbol, List<string> toks)
    {
        string name = symbol.ToLowerInvariant();
        HashSet<string> parts = new(StringComparer.OrdinalIgnoreCase) { name };
        foreach (string part in SymbolWordBoundary().SplitOrWhole(symbol))
            if (part.Length > 0) parts.Add(part.ToLowerInvariant());
        return toks.Any(t => parts.Contains(t));
    }

    private static string CapLines(List<string> lines, int max = 40)
    {
        if (lines.Count <= max) return string.Join("\n", lines);
        return string.Join("\n", lines.Take(max)) + $"\n(+{lines.Count - max} more line(s) truncated — narrow the query)";
    }

    private static string CliMatch(string terms) => string.Join(" OR ", CliTokens(terms).Select(t => $"\"{t}\""));
    private static string McpMatch(string terms) => string.Join(" OR ", McpTokens(terms).Select(t => $"\"{t}\""));

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

    [GeneratedRegex("_|(?<=[a-z0-9])(?=[A-Z])", RegexOptions.None, RegexTimeout.Milliseconds)]
    private static partial Regex SymbolWordBoundary();
}
