using System.Diagnostics;
using System.Text;
using Grimora.Store.Tools;
using Microsoft.Data.Sqlite;

namespace Grimora.Brain.Tools;

/// <summary>
/// The built-in accuracy benchmark: a fixed set of answerable and unanswerable questions against the
/// verified-facts channel, each expecting either the right top hit or a refusal. Copied verbatim from
/// grimora.cs's <c>Eval</c> (grimora.cs:2375), with a self-contained copy of the ranking logic it needs — the
/// same FTS5-plus-bm25 search, relevance floor and coverage gate as grimora.cs's <c>Search</c>/<c>Tokens</c>
/// (grimora.cs:754, grimora.cs:743), which <see cref="Grimora.Facts.Tools.QueryTool"/> also carries its own copy
/// of for the same reason (RESTRUCTURE.md section 2.2: each mover copies rather than shares so the
/// oracles stay independently traceable). CLI-only (no MCP counterpart).
/// </summary>
public sealed class EvalTool : ITool
{
    // Calibrated on real data (grimora.cs:58-64): on-target queries score <= -8.6, generic single-token
    // noise scores >= -2.2; -3.0 sits cleanly in the gap. Only trusted once the corpus is large enough
    // for IDF to discriminate (below that, return the best match rather than over-refuse a young store).
    private const double RelevanceFloor = -3.0;
    private const int FloorMinCorpus = 20;

    private static readonly HashSet<string> Stop = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "is", "a", "an", "of", "to", "in", "on", "for", "and", "or", "what", "how", "are", "does",
        "do", "it", "its", "be", "this", "that", "with", "as", "at", "by", "my", "i", "you", "we", "there",
        "was", "were", "which", "when", "where", "name", "called", "get", "got", "me", "us", "about",
    };

    private static readonly (string q, string expect, bool answerable)[] DefaultQuestions =
    [
        ("media base url", "raw.githubusercontent.com", true),
        ("is unauthorized response 401 or 403", "403", true),
        ("how many database contexts are there", "THREE", true),
        ("what is the tv shows table called", "Tvs", true),
        ("default server port", "7626", true),
        ("storage driver types is smb supported", "nfs", true),
        ("video item watch progress field name", "timestamp", true),
        ("signalr hub routes", "videoHub", true),
        ("encoder preset profile field name", "profile_json", true),
        ("windows installer release asset name", "ExampleInstaller", true),
        ("quantumblockchain unicornrecipe zzz", "", false),
        ("weather forecast lovely saturday", "", false),
        ("server", "", false),
        ("list my favorite colors today", "", false),
    ];

    public string Name => "eval";
    public string CliVerb => "eval";
    public string? McpName => null;
    public string Help =>
        "eval                                    run the built-in accuracy benchmark (fixed answerable/" +
        "unanswerable questions) against the verified-facts channel. No args.";

    public string ExecuteCli(SqliteConnection connection)
    {
        StringBuilder sb = new();
        int hits = 0;
        double totalMs = 0;
        foreach ((string q, string expect, bool answerable) in DefaultQuestions)
        {
            (List<(string term, string category, string value, string source, double score)> rows, double ms) = Search(connection, q, 1);
            totalMs += ms;
            bool ok;
            string shown;
            if (answerable)
            {
                string top = rows.Count > 0 ? rows[0].value : "(no result)";
                ok = rows.Count > 0 && top.Contains(expect, StringComparison.OrdinalIgnoreCase);
                shown = top.Length <= 56 ? top : top[..56] + "…";
            }
            else
            {
                ok = rows.Count == 0;
                shown = rows.Count == 0 ? "(correctly refused)" : "LEAKED: " + (rows[0].value.Length <= 44 ? rows[0].value : rows[0].value[..44] + "…");
            }
            if (ok) hits++;
            sb.AppendLine($"{(ok ? "PASS" : "FAIL")}  {ms,5:F2}ms  \"{q}\"  {(answerable ? "expect '" + expect + "'" : "expect REFUSE")}  ->  {shown}");
        }
        // A literal LF, not AppendLine's platform newline — grimora.cs's old Eval() built this same blank
        // line via Console.WriteLine($"\naccuracy...") (a bare "\n" ahead of the WriteLine's own
        // trailing CRLF), so an AppendLine() here would double the line ending once the CLI case wraps
        // this whole string in its own Console.WriteLine.
        sb.Append('\n');
        sb.Append($"accuracy {hits}/{DefaultQuestions.Length} ({100.0 * hits / DefaultQuestions.Length:F0}%)   avg {totalMs / DefaultQuestions.Length:F2}ms");
        return sb.ToString();
    }

    // FTS5 MATCH isolated in a subquery so it actually filters (a JOIN+MATCH leaked all rows).
    // bm25 lower = better; the score is returned so callers can apply a relevance gate.
    private static (List<(string term, string category, string value, string source, double score)> rows, double ms) Search(SqliteConnection connection, string terms, int limit)
    {
        List<(string, string, string, string, double)> rows = [];
        string match = BuildMatch(terms);
        if (match.Length == 0) return (rows, 0);
        List<string> qToks = Tokens(terms);
        bool gate = ScalarLong(connection, "SELECT count(*) FROM facts") >= FloorMinCorpus;
        Stopwatch sw = Stopwatch.StartNew();
        using SqliteCommand c = connection.CreateCommand();
        c.CommandText = """
            SELECT f.term,f.category,f.value,f.source,m.score,f.aliases,f.notes
            FROM (SELECT k, bm25(facts_fts) AS score FROM facts_fts WHERE facts_fts MATCH $m ORDER BY score LIMIT $l) m
            JOIN facts f ON f.k=m.k ORDER BY m.score
            """;
        c.Parameters.AddWithValue("$m", match);
        c.Parameters.AddWithValue("$l", limit);
        using SqliteDataReader r = c.ExecuteReader();
        while (r.Read())
        {
            double score = r.GetDouble(4);
            if (gate && score > RelevanceFloor) continue; // weak/low-IDF match in a populated store — refuse rather than surface noise
            string hay = $"{r.GetString(0)} {r.GetString(5)} {r.GetString(1)} {r.GetString(2)} {r.GetString(6)}".ToLowerInvariant();
            int present = qToks.Count(t => hay.Contains(t, StringComparison.Ordinal));
            if (qToks.Count >= 3 && present <= 1) continue;
            rows.Add((r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), score));
        }
        sw.Stop();
        return (rows, sw.Elapsed.TotalMilliseconds);
    }

    // Split on every non-alphanumeric, not just space, so a kebab-case or path-shaped term stays
    // searchable by the words it is actually made of.
    private static List<string> Tokens(string terms) =>
        [.. terms.ToLowerInvariant()
            .Split(" \t\r\n-_./\\,;:()[]{}<>\"'`|!?*+=&#@~%$^".ToCharArray(), StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.All(char.IsLetterOrDigit) && t.Length > 1 && !Stop.Contains(t))
            .Distinct()];

    private static string BuildMatch(string terms) => string.Join(" OR ", Tokens(terms).Select(t => $"\"{t}\""));

    private static long ScalarLong(SqliteConnection connection, string sql)
    {
        using SqliteCommand c = connection.CreateCommand();
        c.CommandText = sql;
        return (long)(c.ExecuteScalar() ?? 0L);
    }
}
