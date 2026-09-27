using Microsoft.Data.Sqlite;

namespace Aitm.Brain.Data;

/// <summary>
/// Graph-walk helpers shared by <c>BrainPathTool</c>. Copied verbatim from brain-path.mjs's own
/// import, brain-lib.mjs: <c>resolveNode</c> (brain-lib.mjs:373), <c>pathBetween</c> (brain-lib.mjs:392),
/// <c>blastRadius</c> (brain-lib.mjs:433), <c>communities</c> (brain-lib.mjs:295) and <c>communityOf</c>
/// (brain-lib.mjs:368). None of these had a C# port before this slice (RESTRUCTURE.md slice 19, part 3):
/// slices 15-16 ported the FTS-based <c>brain recall</c>/<c>brain impact</c> reads, which are a
/// different query path over the same tables, not this one.
/// </summary>
public static class BrainGraphLib
{
    public sealed record ResolvedNode(string K, string Label);

    /// <summary>Resolve a loose name to a live node: exact key, then exact label, then a contained
    /// label — brain-lib.mjs:373-387.</summary>
    public static ResolvedNode? ResolveNode(SqliteConnection connection, string name)
    {
        string q = (name ?? "").Trim();
        if (q.Length == 0) return null;

        foreach ((string sql, string param) in new[]
        {
            ("SELECT k, label FROM node WHERE valid_to IS NULL AND k = $p LIMIT 1", q),
            ("SELECT k, label FROM node WHERE valid_to IS NULL AND LOWER(label) = LOWER($p) LIMIT 1", q),
            ("SELECT k, label FROM node WHERE valid_to IS NULL AND label LIKE $p ORDER BY length(label) LIMIT 1", $"%{q}%"),
        })
        {
            using SqliteCommand cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("$p", param);
            using SqliteDataReader r = cmd.ExecuteReader();
            if (r.Read()) return new ResolvedNode(r.GetString(0), r.GetString(1));
        }
        return null;
    }

    public sealed record Hop(string From, string Pred, string To, string Dir, string Because);

    public sealed record PathResult(ResolvedNode? From, ResolvedNode? To, List<Hop>? Hops);

    /// <summary>Shortest connection between two nodes, walked in both directions — brain-lib.mjs:392-428.</summary>
    public static PathResult PathBetween(SqliteConnection connection, string fromName, string toName, int maxDepth = 5)
    {
        ResolvedNode? a = ResolveNode(connection, fromName);
        ResolvedNode? b = ResolveNode(connection, toName);
        if (a is null || b is null) return new PathResult(a, b, null);
        if (a.K == b.K) return new PathResult(a, b, []);

        List<(string P, string Other, string Because, string Dir)> Neighbours(string k)
        {
            List<(string, string, string, string)> rows = [];
            using SqliteCommand cmd = connection.CreateCommand();
            cmd.CommandText = """
                SELECT p, o AS other, because, 'out' AS dir FROM triple WHERE valid_to IS NULL AND o_is_literal = 0 AND s = $k
                UNION ALL
                SELECT p, s AS other, because, 'in'  AS dir FROM triple WHERE valid_to IS NULL AND o_is_literal = 0 AND o = $k
                """;
            cmd.Parameters.AddWithValue("$k", k);
            using SqliteDataReader r = cmd.ExecuteReader();
            while (r.Read())
                rows.Add((r.GetString(0), r.GetString(1), r.IsDBNull(2) ? "" : r.GetString(2), r.GetString(3)));
            return rows;
        }

        HashSet<string> seen = [a.K];
        List<(string K, List<Hop> Hops)> frontier = [(a.K, new List<Hop>())];
        for (int depth = 0; depth < maxDepth; depth++)
        {
            List<(string K, List<Hop> Hops)> next = [];
            foreach ((string k, List<Hop> hops) in frontier)
            {
                foreach ((string p, string other, string because, string dir) in Neighbours(k))
                {
                    if (!seen.Add(other)) continue;
                    List<Hop> newHops = [.. hops, new Hop(k, p, other, dir, because)];
                    if (other == b.K) return new PathResult(a, b, newHops);
                    next.Add((other, newHops));
                }
            }
            if (next.Count == 0) break;
            frontier = next;
        }
        return new PathResult(a, b, null);
    }

    public sealed record BlastRow(string Symbol, long Files, long Projects, string Names);

    /// <summary>Symbols a file declares that also appear elsewhere — brain-lib.mjs:433-454.</summary>
    public static List<BlastRow> BlastRadius(SqliteConnection connection, string filePath, int limit = 5)
    {
        string norm = filePath.Replace('\\', '/');
        List<BlastRow> rows = [];
        try
        {
            using SqliteCommand cmd = connection.CreateCommand();
            cmd.CommandText = """
                SELECT e.symbol,
                       (SELECT COUNT(DISTINCT a.file) FROM edges a WHERE a.symbol = e.symbol) AS files,
                       (SELECT COUNT(DISTINCT a.project) FROM edges a WHERE a.symbol = e.symbol AND a.project IS NOT NULL) AS projects,
                       (SELECT GROUP_CONCAT(DISTINCT a.project) FROM edges a WHERE a.symbol = e.symbol AND a.project IS NOT NULL) AS names
                FROM edges e
                WHERE LOWER(e.file) = LOWER($f)
                GROUP BY e.symbol
                HAVING projects > 1 OR files > 1
                ORDER BY projects DESC, files DESC
                LIMIT $l
                """;
            cmd.Parameters.AddWithValue("$f", norm);
            cmd.Parameters.AddWithValue("$l", limit);
            using SqliteDataReader r = cmd.ExecuteReader();
            while (r.Read())
                rows.Add(new BlastRow(r.GetString(0), r.GetInt64(1), r.GetInt64(2), r.IsDBNull(3) ? "" : r.GetString(3)));
        }
        catch (SqliteException)
        {
            return [];
        }
        return rows;
    }

    public sealed record CommunityMember(string Key, string Label, string Kind);

    public sealed record Community(string Hub, string HubKey, int Size, List<CommunityMember> Members);

    /// <summary>Label-propagation communities over the triple graph — brain-lib.mjs:295-365.</summary>
    public static List<Community> Communities(SqliteConnection connection, int rounds = 8)
    {
        List<(string S, string P, string O)> rows = [];
        try
        {
            using SqliteCommand cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT s, p, o FROM triple WHERE valid_to IS NULL AND o_is_literal = 0";
            using SqliteDataReader r = cmd.ExecuteReader();
            while (r.Read()) rows.Add((r.GetString(0), r.GetString(1), r.GetString(2)));
        }
        catch (SqliteException)
        {
            return [];
        }
        if (rows.Count == 0) return [];

        Dictionary<string, int> freq = [];
        foreach ((string _, string p, string _) in rows) freq[p] = freq.GetValueOrDefault(p) + 1;
        double WeightOf(string p) => 1.0 / (1.0 + Math.Log(1 + freq.GetValueOrDefault(p, 1)));

        Dictionary<string, List<(string Other, double W)>> adj = [];
        void Link(string a, string b, string p)
        {
            if (!adj.TryGetValue(a, out List<(string, double)>? list)) { list = []; adj[a] = list; }
            list.Add((b, WeightOf(p)));
        }
        foreach ((string s, string p, string o) in rows) { Link(s, o, p); Link(o, s, p); }

        Dictionary<string, string> label = [];
        foreach (string k in adj.Keys) label[k] = k;
        List<string> keys = [.. adj.Keys.OrderBy(k => k, StringComparer.Ordinal)];

        for (int i = 0; i < rounds; i++)
        {
            int moved = 0;
            foreach (string k in keys)
            {
                Dictionary<string, double> tally = [];
                foreach ((string other, double w) in adj[k])
                {
                    string nl = label[other];
                    tally[nl] = tally.GetValueOrDefault(nl) + w;
                }
                if (tally.Count == 0) continue;
                string best = tally
                    .OrderByDescending(kv => kv.Value)
                    .ThenBy(kv => kv.Key, StringComparer.Ordinal)
                    .First().Key;
                if (best != label[k]) { label[k] = best; moved++; }
            }
            if (moved == 0) break;
        }

        Dictionary<string, List<string>> groups = [];
        foreach ((string node, string lab) in label)
        {
            if (!groups.TryGetValue(lab, out List<string>? list)) { list = []; groups[lab] = list; }
            list.Add(node);
        }

        Dictionary<string, (string Label, string Kind)> named = [];
        try
        {
            using SqliteCommand cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT k, label, kind FROM node WHERE valid_to IS NULL";
            using SqliteDataReader r = cmd.ExecuteReader();
            while (r.Read()) named[r.GetString(0)] = (r.GetString(1), r.IsDBNull(2) ? "" : r.GetString(2));
        }
        catch (SqliteException) { /* node table absent */ }

        return [.. groups.Values
            .Where(members => members.Count > 1)
            .Select(members =>
            {
                List<string> sorted = [.. members.OrderByDescending(m => adj.TryGetValue(m, out List<(string, double)>? l) ? l.Count : 0)];
                string hub = sorted[0];
                return new Community(
                    named.TryGetValue(hub, out (string Label, string Kind) hn) ? hn.Label : hub,
                    hub,
                    members.Count,
                    [.. sorted.Select(m => new CommunityMember(
                        m,
                        named.TryGetValue(m, out (string Label, string Kind) mn) ? mn.Label : m,
                        named.TryGetValue(m, out (string Label, string Kind) mk) ? mk.Kind : ""))]);
            })
            .OrderByDescending(c => c.Size)];
    }

    /// <summary>The cluster a given node belongs to — brain-lib.mjs:368-370.</summary>
    public static Community? CommunityOf(SqliteConnection connection, string nodeKey, int rounds = 8) =>
        Communities(connection, rounds).FirstOrDefault(c => c.Members.Any(m => m.Key == nodeKey));
}
