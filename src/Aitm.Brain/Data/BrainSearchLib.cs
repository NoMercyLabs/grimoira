using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace Aitm.Brain.Data;

/// <summary>
/// The in-memory scoring layer shared by the brain hooks (brain-gate, brain-harvest, brain-capture),
/// ported for its regression suite <c>brain-lib.test.mjs</c> (RESTRUCTURE.md slice 19, part 3). Copied
/// verbatim from brain-lib.mjs: <c>tokenize</c> (brain-lib.mjs:44), <c>signature</c> (brain-lib.mjs:59),
/// <c>search</c> (brain-lib.mjs:145), <c>historyFor</c> (brain-lib.mjs:209). Node's bundled SQLite has no
/// FTS5, so brain-lib.mjs scores every candidate itself; the C# host scores through SQLite FTS5 instead
/// (see <c>BrainRecallTool</c>), so this port exists to keep the hooks' own oracle green, not because the
/// CLI or MCP side needs it — no tool in this project calls it yet.
/// </summary>
public static partial class BrainSearchLib
{
    private static readonly HashSet<string> Stop = new(StringComparer.Ordinal)
    {
        "the", "is", "a", "an", "of", "to", "in", "on", "for", "and", "or", "what", "how", "are", "does",
        "do", "it", "its", "be", "this", "that", "with", "as", "at", "by", "my", "i", "you", "we", "there",
        "was", "were", "which", "when", "where", "name", "called", "get", "got", "me", "us", "about",
        "can", "please", "lets", "let", "make", "need", "want", "should", "would", "could", "will", "just",
        "now", "then", "fix", "add", "use", "run", "check", "look", "help", "why", "not", "but", "if", "so",
        "new", "all", "any", "from", "into", "out", "up", "down", "has", "have", "had", "been", "null",
    };


    public static List<string> Tokenize(string? raw, bool splitIdentifiers = true)
    {
        string cleaned = MyRegex1().Replace(MyRegex().Replace(raw ?? "", " "), " ");
        List<string> words = [];
        foreach (string chunk in cleaned.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            words.Add(chunk);
            if (splitIdentifiers)
                foreach (string part in MyRegex2().Split(chunk))
                    if (part.Length > 0 && part != chunk) words.Add(part);
        }
        return [.. words
            .Select(w => w.ToLowerInvariant())
            .Where(w => w.Length > 2 && !Stop.Contains(w))
            .Distinct()
            .Take(8)];
    }

    public static string Signature(IEnumerable<string> tokens) => string.Join(" ", tokens.OrderBy(t => t, StringComparer.Ordinal));

    public sealed record SearchHit(string Head, string Body, string KindTag, bool Hard, double Score);

    private sealed record Channel(string Kind, string Sql, string[] Cols, double Weight, double HeadMax, bool Headless, int MaxPerKind);

    private static readonly Channel[] Channels =
    [
        new("fact", "SELECT term AS head, substr(value,1,600) AS body, 0 AS hard, 0 AS hits FROM facts WHERE {W}",
            ["term", "aliases", "value"], 1.6, double.PositiveInfinity, false, 0),
        new("rule", "SELECT hook AS head, substr(body,1,600) AS body, hard AS hard, 0 AS hits FROM memory WHERE {W}",
            ["hook", "title", "body"], 1.5, double.PositiveInfinity, false, 0),
        new("node", "SELECT substr(n.label,1,300) AS head, substr(n.gloss,1,600) AS body, n.hard AS hard, " +
            "COALESCE(u.hits,0) AS hits, COALESCE(n.scheme,'') AS scheme FROM node_now n " +
            "LEFT JOIN usage u ON u.node_k = n.k WHERE {W}",
            ["n.label", "n.gloss", "n.k"], 0.9, 90, false, 0),
        new("doc", "SELECT title AS head, substr(content,1,600) AS body, 0 AS hard, 0 AS hits, path AS loc FROM docs WHERE {W}",
            ["title", "content", "terms"], 1.0, double.PositiveInfinity, false, 0),
        new("code", "SELECT symbol AS head, usage AS body, 0 AS hard, 0 AS hits, " +
            "(file || ':' || COALESCE(line,0)) AS loc, file AS srcfile FROM edges WHERE {W}",
            ["symbol", "file", "usage"], 1.8, 90, false, 0),
        new("chat", "SELECT '' AS head, substr(text,1,600) AS body, 0 AS hard, 0 AS hits, ts AS loc FROM chat WHERE {W}",
            ["text"], 0.8, double.PositiveInfinity, true, 2),
    ];

    private sealed class RawRow
    {
        public string Head = "";
        public string Body = "";
        public bool Hard;
        public long Hits;
        public string Scheme = "";
        public string Srcfile = "";
        public string Kind = "";
        public double Weight;
        public double HeadMax;
        public bool Headless;
        public int MaxPerKind;
    }

    private static List<RawRow> RunChannel(SqliteConnection connection, Channel ch, List<string> tokens)
    {
        List<RawRow> rows = [];
        string where = string.Join(" OR ", tokens.Select((_, i) =>
            "(" + string.Join(" OR ", ch.Cols.Select((c, j) => $"{c} LIKE $p{i}_{j}")) + ")"));
        try
        {
            using SqliteCommand cmd = connection.CreateCommand();
            cmd.CommandText = ch.Sql.Replace("{W}", where);
            for (int i = 0; i < tokens.Count; i++)
                for (int j = 0; j < ch.Cols.Length; j++)
                    cmd.Parameters.AddWithValue($"$p{i}_{j}", $"%{tokens[i]}%");
            using SqliteDataReader r = cmd.ExecuteReader();
            Dictionary<string, int> ord = [];
            for (int i = 0; i < r.FieldCount; i++) ord[r.GetName(i)] = i;
            string GetStr(string name) => ord.TryGetValue(name, out int i) && !r.IsDBNull(i) ? Convert.ToString(r.GetValue(i)) ?? "" : "";
            long GetLong(string name) => ord.TryGetValue(name, out int i) && !r.IsDBNull(i) ? Convert.ToInt64(r.GetValue(i)) : 0;
            while (r.Read())
            {
                rows.Add(new RawRow
                {
                    Head = GetStr("head"),
                    Body = GetStr("body"),
                    Hard = GetLong("hard") != 0,
                    Hits = GetLong("hits"),
                    Scheme = GetStr("scheme"),
                    Srcfile = GetStr("srcfile"),
                    Kind = ch.Kind,
                    Weight = ch.Weight,
                    HeadMax = ch.HeadMax,
                    Headless = ch.Headless,
                    MaxPerKind = ch.MaxPerKind,
                });
            }
        }
        catch (SqliteException)
        {
            // channel absent in this store
        }
        return rows;
    }

    private static readonly HashSet<string> IdentitySchemes = new(StringComparer.Ordinal)
        { "repo", "org", "host", "service", "registry", "domain" };


    private static bool SymbolMatches(string symbol, List<string> tokens)
    {
        string name = (symbol ?? "").ToLowerInvariant();
        if (name.Length == 0) return false;
        HashSet<string> parts = new(StringComparer.Ordinal) { name };
        foreach (string part in MyRegex2().Split(symbol ?? ""))
            if (part.Length > 0) parts.Add(part.ToLowerInvariant());
        return tokens.Any(parts.Contains);
    }

    public static List<SearchHit> Search(SqliteConnection connection, List<string> tokens, int limit = 5, IReadOnlyCollection<string>? exclude = null)
    {
        if (tokens.Count == 0) return [];
        exclude ??= Array.Empty<string>();

        List<RawRow> rows = [];
        foreach (Channel ch in Channels)
        {
            if (exclude.Contains(ch.Kind)) continue;
            rows.AddRange(RunChannel(connection, ch, tokens));
        }

        int minCover = tokens.Count >= 3 ? 2 : 1;

        List<(RawRow Row, string Head, string Body, int Cover, double Score)> scored = [];
        foreach (RawRow r in rows)
        {
            string head = r.Head;
            string body = r.Body;
            if (head.Length > (double.IsPositiveInfinity(r.HeadMax) ? int.MaxValue : r.HeadMax))
            {
                body = $"{head} {body}";
                head = "";
            }
            string headHay = head.ToLowerInvariant();
            string bodyHay = body.Length > 500 ? body[..500].ToLowerInvariant() : body.ToLowerInvariant();
            int headCover = tokens.Count(t => headHay.Contains(t));
            int bodyCover = tokens.Count(t => !headHay.Contains(t) && bodyHay.Contains(t));
            double blobPenalty = (body.Length > 600 ? 1.5 : 0) + (head.Length == 0 && !r.Headless ? 2 : 0);
            double identity = r.Kind == "node" && IdentitySchemes.Contains(r.Scheme) ? 3 : 0;
            double testPenalty = r.Kind == "code" && MyRegex3().IsMatch(r.Srcfile ?? "") ? 2.5 : 0;
            double baseScore = headCover * 3 + bodyCover + Math.Log2((r.Hits) + 1) * 0.5 + (r.Hard ? 2 : 0)
                + identity - blobPenalty - testPenalty;
            int cover = headCover + bodyCover;
            double score = baseScore * r.Weight;
            if (cover < minCover || score <= 0) continue;
            if (r.Kind == "code" && !SymbolMatches(head, tokens)) continue;
            scored.Add((r, head, body, cover, score));
        }

        scored = [.. scored.OrderByDescending(s => s.Score)];

        HashSet<string> seen = [];
        Dictionary<string, int> perKind = [];
        List<SearchHit> picks = [];
        foreach ((RawRow row, string head, string body, int _, double score) in scored)
        {
            string key = (head.Length > 0 ? head : body).ToLowerInvariant();
            key = key.Length > 80 ? key[..80] : key;
            if (seen.Contains(key)) continue;
            int used = perKind.GetValueOrDefault(row.Kind);
            if (row.MaxPerKind > 0 && used >= row.MaxPerKind) continue;
            seen.Add(key);
            perKind[row.Kind] = used + 1;
            picks.Add(new SearchHit(head, body, row.Kind, row.Hard, score));
            if (picks.Count == limit) break;
        }
        return picks;
    }


    public static List<SearchHit> HistoryFor(SqliteConnection connection, string filePath, int limit = 4)
    {
        string norm = filePath.Replace('\\', '/');
        string baseName = norm.Split('/').Last();
        string[] segs = norm.Split('/');
        string tail = string.Join("/", segs.Skip(Math.Max(0, segs.Length - 2)));

        string stem = MyRegex5().Replace(baseName, "");
        int humps = stem.Count(char.IsUpper);
        List<string> needles = [tail, baseName];
        if (stem.Length >= 10 || humps >= 2) needles.Add(stem);

        List<(string Kind, string Sql)> literalChannels =
        [
            ("rule", "SELECT hook AS head, substr(body,1,600) AS body, hard FROM memory WHERE body LIKE $a OR hook LIKE $a"),
            ("fact", "SELECT term AS head, substr(value,1,600) AS body, 0 AS hard FROM facts WHERE value LIKE $a OR term LIKE $a"),
            ("doc", "SELECT title AS head, substr(content,1,600) AS body, 0 AS hard FROM docs WHERE content LIKE $a OR (title || ' ' || COALESCE(terms,'')) LIKE $a"),
            ("chat", "SELECT '' AS head, substr(text,1,600) AS body, 0 AS hard FROM chat WHERE text LIKE $a OR text LIKE $a"),
        ];

        List<SearchHit> literal = [];
        foreach ((string kind, string sql) in literalChannels)
        {
            foreach (string needle in needles)
            {
                try
                {
                    using SqliteCommand cmd = connection.CreateCommand();
                    cmd.CommandText = sql;
                    cmd.Parameters.AddWithValue("$a", $"%{needle}%");
                    using SqliteDataReader r = cmd.ExecuteReader();
                    while (r.Read())
                    {
                        string head = r.IsDBNull(0) ? "" : Convert.ToString(r.GetValue(0)) ?? "";
                        string body = r.IsDBNull(1) ? "" : Convert.ToString(r.GetValue(1)) ?? "";
                        bool hard = !r.IsDBNull(2) && Convert.ToInt64(r.GetValue(2)) != 0;
                        literal.Add(new SearchHit(head, body, kind, hard, 100));
                    }
                }
                catch (SqliteException) { /* channel absent */ }
            }
        }

        List<SearchHit> symbolic = [];
        try
        {
            List<string> symTokens = [];
            using (SqliteCommand cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT DISTINCT symbol FROM edges WHERE LOWER(file) = LOWER($f) LIMIT 12";
                cmd.Parameters.AddWithValue("$f", norm);
                using SqliteDataReader r = cmd.ExecuteReader();
                while (r.Read())
                {
                    string s = (r.IsDBNull(0) ? "" : Convert.ToString(r.GetValue(0)) ?? "").ToLowerInvariant();
                    if (s.Length >= 5) symTokens.Add(s);
                }
            }
            symTokens = [.. symTokens.Take(6)];
            if (symTokens.Count >= 2)
                symbolic = Search(connection, symTokens, limit * 2, new[] { "code" });
        }
        catch (SqliteException) { /* edges absent */ }

        HashSet<string> seen = [];
        List<SearchHit> picks = [];
        foreach (SearchHit r in literal.Concat(symbolic))
        {
            string body = (r.Body ?? "").Trim();
            if (MyRegex4().IsMatch(body)) continue;
            string key = (r.Head.Length > 0 ? r.Head : body).ToLowerInvariant();
            key = key.Length > 80 ? key[..80] : key;
            if (key.Length == 0 || seen.Contains(key)) continue;
            seen.Add(key);
            picks.Add(r);
            if (picks.Count == limit) break;
        }
        return picks;
    }

    [GeneratedRegex(@"\\[a-zA-Z]", RegexOptions.Compiled)]
    private static partial Regex MyRegex();
    [GeneratedRegex(@"[(){}\[\]|^$*+?.\\/'""`<>=!,;:#@&%~-]", RegexOptions.Compiled)]
    private static partial Regex MyRegex1();
    [GeneratedRegex(@"_|(?<=[a-z0-9])(?=[A-Z])", RegexOptions.Compiled)]
    private static partial Regex MyRegex2();
    [GeneratedRegex(@"(^|[\\/])(tests?|spec|__tests__)[\\/]", RegexOptions.IgnoreCase | RegexOptions.Compiled, "nl-NL")]
    private static partial Regex MyRegex3();
    [GeneratedRegex(@"^(this session is being continued|<\?xml|summary:|caveat: the messages below)", RegexOptions.IgnoreCase | RegexOptions.Compiled, "nl-NL")]
    private static partial Regex MyRegex4();
    [GeneratedRegex(@"\.[^.]+$")]
    private static partial Regex MyRegex5();
}
