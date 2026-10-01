using Microsoft.Data.Sqlite;
using Grimoira.Store.Tools;

namespace Grimoira.Graph.Tools;

/// <summary>
/// Shortest path between two symbols/files over the code graph — "what connects A to B" without a
/// human tracing declaration-then-usage chains by hand. CLI verb <c>graph-path</c> and MCP tool
/// <c>graph_path</c> are one job with two shapes today (RESTRUCTURE.md section 2.2); both are copied
/// verbatim from its oracle: <c>ExecuteCli</c> from grimoira.cs's <c>GraphPathCmd</c>/<c>GraphBfs</c>
/// (grimoira.cs:2706, grimoira.cs:2558). grimoira.cs's and mcp.cs's <c>graph_path</c>/<c>GraphBfs</c> (mcp.cs:1203,
/// mcp.cs:1062) produce byte-identical output for every message and hop line, so <c>ExecuteMcp</c>
/// delegates to <c>ExecuteCli</c> rather than keeping a second, textually-identical copy of the same
/// BFS — unlike <c>ImpactTool</c>/<c>QueryTool</c>, whose CLI and MCP oracles genuinely disagree.
///
/// The lazy <c>CREATE INDEX IF NOT EXISTS edges_file_idx</c> inside each BFS is kept as-is: it is the
/// same on-demand index GraphSchema's doc comment already flags as belonging in the schema once the
/// graph-indexer slice (14) repoints this call site — not resolved here.
/// </summary>
public sealed class GraphPathTool : ITool
{
    public string Name => "graph-path";
    public string CliVerb => "graph-path";
    public string McpName => "graph_path";
    public string Help =>
        "graph-path <a> <b>                    shortest path between two symbols/files (max depth 6). " +
        "MCP graph_path(a, b): same lookup.";

    public string ExecuteCli(SqliteConnection connection, string fromInput, string toInput)
    {
        (string kind, string value)? from = ResolveGraphNode(connection, fromInput);
        (string kind, string value)? to = ResolveGraphNode(connection, toInput);
        if (from == null) return $"no symbol or file matches \"{fromInput}\".";
        if (to == null) return $"no symbol or file matches \"{toInput}\".";

        List<(string toKind, string toVal, string file, int line, string rel)>? hops = GraphBfs(connection, from.Value, to.Value);
        if (hops == null) return $"no path found between \"{fromInput}\" and \"{toInput}\" within depth 6.";
        if (hops.Count == 0) return "same node.";

        return FormatHops(connection, from.Value, to.Value, hops);
    }

    public string ExecuteMcp(SqliteConnection connection, string a, string b) => ExecuteCli(connection, a, b);

    private static string FormatHops(SqliteConnection connection, (string kind, string value) from, (string kind, string value) to,
        List<(string toKind, string toVal, string file, int line, string rel)> hops)
    {
        bool hasFileRel = Schema.GraphFileRelSchema.HasColumn(connection, "edges", "file_rel");
        Dictionary<string, string> roots = hasFileRel ? Schema.GraphFileRelSchema.LoadProjectRoots(connection) : [];

        // Slice 31: a hop's file (its own toVal, or the file that carries a usage hop's symbol) is
        // resolved to a full path for THIS machine via file_rel, falling back to the stored file value.
        string ResolveHopFile(string file)
        {
            if (!hasFileRel) return file;
            using SqliteCommand c = connection.CreateCommand();
            c.CommandText = "SELECT project, file_rel FROM edges WHERE file=$f AND file_rel IS NOT NULL LIMIT 1";
            c.Parameters.AddWithValue("$f", file);
            using SqliteDataReader r = c.ExecuteReader();
            if (!r.Read()) return file;
            string project = r.GetString(0);
            string? fileRel = r.IsDBNull(1) ? null : r.GetString(1);
            return Schema.GraphFileRelSchema.ResolveFull(roots.GetValueOrDefault(project), fileRel, file);
        }

        List<string> lines =
        [
            $"[{from.kind}] {from.value}  ->  [{to.kind}] {to.value}   ({hops.Count} hop(s))"
        ];
        foreach ((string toKind, string toVal, string file, int line, string rel) h in hops)
        {
            string resolvedFile = ResolveHopFile(h.file);
            string resolvedToVal = h.toKind == "file" ? ResolveHopFile(h.toVal) : h.toVal;
            string loc = h.toKind == "file" ? $"line {h.line}" : $"{resolvedFile}:{h.line}";
            lines.Add($"  {h.rel}  [{h.toKind}] {resolvedToVal}   {loc}");
        }
        return CapLines(lines);
    }

    private static string CapLines(List<string> lines, int max = 40)
    {
        if (lines.Count <= max) return string.Join("\n", lines);
        return string.Join("\n", lines.Take(max)) + $"\n(+{lines.Count - max} more line(s) truncated — narrow the query)";
    }

    // Resolve a loose name to a live graph node: an exact symbol (any case) first, then a file whose
    // path ends with the given text. Symbols and files share no namespace, so the first hit is unambiguous.
    private static (string kind, string value)? ResolveGraphNode(SqliteConnection con, string input)
    {
        string norm = input.Trim();
        if (norm.Length == 0) return null;
        using (SqliteCommand c = con.CreateCommand())
        {
            c.CommandText = "SELECT symbol FROM edges WHERE symbol=$s COLLATE NOCASE LIMIT 1";
            c.Parameters.AddWithValue("$s", norm);
            if (c.ExecuteScalar() is string sym) return ("symbol", sym);
        }
        using (SqliteCommand c = con.CreateCommand())
        {
            c.CommandText = "SELECT file FROM edges WHERE file LIKE $f LIMIT 1";
            c.Parameters.AddWithValue("$f", "%" + norm.Replace('\\', '/'));
            if (c.ExecuteScalar() is string file) return ("file", file);
        }
        return null;
    }

    // Breadth-first search over the symbol<->file bipartite graph edges gives us for free: a symbol's
    // hop is its declaration/usage files, a file's hop is the symbols it carries. Depth is capped at 6
    // hops, and each node's fan-out is capped too — copied verbatim from grimoira.cs's GraphBfs (grimoira.cs:2558).
    private static List<(string toKind, string toVal, string file, int line, string rel)>? GraphBfs(
        SqliteConnection con, (string kind, string value) from, (string kind, string value) to, int maxDepth = 6)
    {
        using (SqliteCommand idx = con.CreateCommand())
        {
            idx.CommandText = "CREATE INDEX IF NOT EXISTS edges_file_idx ON edges(file, symbol, line)";
            idx.ExecuteNonQuery();
        }
        if (from.kind == to.kind && string.Equals(from.value, to.value, StringComparison.OrdinalIgnoreCase)) return [];

        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase) { $"{from.kind}:{from.value}" };
        List<(string kind, string value, List<(string toKind, string toVal, string file, int line, string rel)> hops)> frontier =
        [
            (from.kind, from.value, new List<(string, string, string, int, string)>())
        ];

        int expansions = 2000;
        for (int depth = 0; depth < maxDepth; depth++)
        {
            List<(string kind, string value, List<(string toKind, string toVal, string file, int line, string rel)> hops)> next = [];
            foreach ((string kind, string value, List<(string toKind, string toVal, string file, int line, string rel)> hops) cur in frontier)
            {
                if (expansions-- <= 0) return null;
                using SqliteCommand c = con.CreateCommand();
                string rel = cur.kind == "symbol" ? "defined-in" : "uses";
                c.CommandText = cur.kind == "symbol"
                    ? "SELECT DISTINCT file, line FROM edges WHERE symbol=$k LIMIT 60"
                    : "SELECT DISTINCT symbol, line FROM edges WHERE file=$k LIMIT 60";
                c.Parameters.AddWithValue("$k", cur.value);
                using SqliteDataReader r = c.ExecuteReader();
                while (r.Read())
                {
                    string neighborVal = r.GetString(0);
                    int line = r.IsDBNull(1) ? 0 : r.GetInt32(1);
                    string neighborKind = cur.kind == "symbol" ? "file" : "symbol";
                    string key = $"{neighborKind}:{neighborVal}";
                    if (!seen.Add(key)) continue;
                    string hopFile = cur.kind == "symbol" ? neighborVal : cur.value;
                    List<(string toKind, string toVal, string file, int line, string rel)> hops = [.. cur.hops, (neighborKind, neighborVal, hopFile, line, rel)];
                    if (neighborKind == to.kind && string.Equals(neighborVal, to.value, StringComparison.OrdinalIgnoreCase)) return hops;
                    next.Add((neighborKind, neighborVal, hops));
                }
            }
            if (next.Count == 0) break;
            frontier = next;
        }
        return null;
    }
}
