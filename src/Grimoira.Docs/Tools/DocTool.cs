using System.Text;
using Grimoira.Store.Data;
using Grimoira.Store.Tools;
using Microsoft.Data.Sqlite;

namespace Grimoira.Docs.Tools;

/// <summary>
/// Recall channel for absorbed docs (separate from facts and chat). CLI verb <c>doc</c> and MCP tool
/// <c>doc</c> are one job with two shapes today (RESTRUCTURE.md section 2.2): <c>ExecuteCli</c> is copied
/// verbatim from grimoira.cs's <c>DocCmd</c>/<c>PrintDocs</c> (grimoira.cs:1018/1037), <c>ExecuteMcp</c> from
/// mcp.cs's <c>doc</c> (mcp.cs:479) — neither reinforces the usage signal, matching both oracles.
/// </summary>
public sealed class DocTool : ITool
{
    private const int CellCap = 160;

    private static readonly HashSet<string> Stop = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "is", "a", "an", "of", "to", "in", "on", "for", "and", "or", "what", "how", "are", "does",
        "do", "it", "its", "be", "this", "that", "with", "as", "at", "by", "my", "i", "you", "we", "there",
        "was", "were", "which", "when", "where", "name", "called", "get", "got", "me", "us", "about",
    };

    // mcp.cs@bbb9b4d's own Stop set (mcp.cs:93) — one word short of the CLI's (no "me"/"us"/"about"),
    // kept separate so the MCP path stays byte-for-byte with the pre-dispatch oracle.
    private static readonly HashSet<string> McpStop = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "is", "a", "an", "of", "to", "in", "on", "for", "and", "or", "what", "how", "are",
        "does", "do", "it", "its", "be", "this", "that", "with", "as", "at", "by", "my", "i", "you",
        "we", "there", "was", "were", "which", "when", "where", "name", "called", "get", "got",
    };

    public string Name => "doc";
    public string CliVerb => "doc";
    public string McpName => "doc";
    public bool IsReadOnly => true;
    public string Help =>
        "doc <terms>                          search absorbed docs (CLI). MCP doc(query): up to 3 hits, gap-logged when empty.";

    public string ExecuteCli(SqliteConnection connection, string terms)
    {
        string match = BuildMatch(terms);
        if (match.Length == 0) return "no usable terms.";

        StringBuilder sb = new();
        // One ranking for synthesis and direct sections alike (top 5 by bm25, best first). The old CLI printed
        // up to 2 synthesis rows first, each with a 20,000-char snippet, and only then the best sections: on a
        // real store the one section holding every query word came 62 lines down, behind an unrelated synthesis.
        int n = PrintDocs(connection, sb, """
            SELECT d.path,d.title,d.category,d.content FROM (SELECT k, bm25(docs_fts) AS score FROM docs_fts WHERE docs_fts MATCH $m ORDER BY score LIMIT 5) m
            JOIN docs d ON d.k=m.k ORDER BY m.score
            """, match);
        if (n == 0) return $"no docs match \"{terms}\".";
        // Each row was appended with AppendLine on top of its own embedded trailing "\n" (grimoira.cs's old
        // PrintDocs did the same via one Console.WriteLine per row) — CLI dispatch wraps this return value
        // in one more Console.WriteLine, so the AppendLine terminator on the LAST row would double up
        // into an extra blank line the old CLI never had. Strip exactly that one terminator; the embedded
        // "\n" stays, so the final Console.WriteLine reproduces the old row's own line ending exactly.
        return StripOneTrailingNewLine(sb.ToString());
    }

    public string ExecuteMcp(SqliteConnection connection, string query)
    {
        string match = McpMatch(query);
        if (match.Length == 0) return "no usable query terms.";
        try
        {
            using SqliteCommand cmd = connection.CreateCommand();
            cmd.CommandText = """
                SELECT d.category,d.title,d.path,d.content
                FROM (SELECT k, bm25(docs_fts) AS s FROM docs_fts WHERE docs_fts MATCH $m ORDER BY s LIMIT 3) x
                JOIN docs d ON d.k=x.k ORDER BY x.s
                """;
            cmd.Parameters.AddWithValue("$m", match);
            StringBuilder sb = new();
            using (SqliteDataReader reader = cmd.ExecuteReader())
            {
                while (reader.Read())
                    sb.AppendLine($"• [{reader.GetString(0)}] {reader.GetString(1)} ({Clip(reader.GetString(2), CellCap)})\n  {Clip(reader.GetString(3), 280)}");
            }
            return sb.Length == 0 ? $"no docs match \"{query}\".{McpLogGap(connection, query)}" : OutputBudget.Clip(sb.ToString());
        }
        catch (SqliteException)
        {
            return "no docs indexed yet for this instance.";
        }
    }

    private static int PrintDocs(SqliteConnection connection, StringBuilder sb, string sql, string match)
    {
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$m", match);
        using SqliteDataReader reader = cmd.ExecuteReader();
        int n = 0;
        while (reader.Read())
        {
            string category = reader.GetString(2);
            string content = reader.GetString(3);
            int cap = category == "synthesis" ? 1500 : 400;
            string snip = content.Length <= cap ? content : content[..cap] + "…";
            sb.AppendLine($"• [{category}] {reader.GetString(1)}  ({reader.GetString(0)})\n  {snip}\n");
            n++;
        }
        return n;
    }

    private static string McpLogGap(SqliteConnection connection, string query)
    {
        try
        {
            string norm = string.Join(' ', McpTokens(query));
            if (norm.Length < 3) return "";
            GapLog.Record(connection, "doc", norm);
            return " [gap logged — stage the answer via brain_stage once you learn it]";
        }
        catch (SqliteException)
        {
            return "";
        }
    }

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..max].TrimEnd() + "…";

    private static string StripOneTrailingNewLine(string s) =>
        s.EndsWith(Environment.NewLine, StringComparison.Ordinal) ? s[..^Environment.NewLine.Length] : s;

    private static string BuildMatch(string terms) => string.Join(" OR ", Tokens(terms).Select(t => $"\"{t}\""));

    private static string McpMatch(string terms) => string.Join(" OR ", McpTokens(terms).Select(t => $"\"{t}\""));

    private static List<string> Tokens(string terms) =>
        [.. terms.ToLowerInvariant()
            .Split(" \t\r\n-_./\\,;:()[]{}<>\"'`|!?*+=&#@~%$^".ToCharArray(), StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.All(char.IsLetterOrDigit) && t.Length > 1 && !Stop.Contains(t))
            .Distinct()];

    // mcp.cs@bbb9b4d's own Tokens (mcp.cs:249): split on spaces only, then strip non-alphanumeric
    // characters out of each token — a hyphenated/punctuated query glues into one token instead of
    // splitting into several the way the CLI's Tokens() (above) does.
    private static List<string> McpTokens(string terms) =>
        [.. terms.ToLowerInvariant()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(t => new string([.. t.Where(char.IsLetterOrDigit)]))
            .Where(t => t.Length > 1 && !McpStop.Contains(t))];
}
