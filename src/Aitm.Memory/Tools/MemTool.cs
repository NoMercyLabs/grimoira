using System.Text;
using Aitm.Store.Data;
using Aitm.Store.Tools;
using Microsoft.Data.Sqlite;

namespace Aitm.Memory.Tools;

/// <summary>
/// Pull-based recall of the migrated rules/preferences. CLI verb <c>mem</c> and MCP tool <c>rule</c> are
/// one job with two shapes today (RESTRUCTURE.md section 2.2): <c>ExecuteCli</c> is copied verbatim from
/// aitm.cs's <c>MemCmd</c> (aitm.cs:1275, plus the <c>--hard</c> branch that lists the always-on core),
/// <c>ExecuteMcp</c> from mcp.cs's <c>rule</c> (mcp.cs:507).
/// </summary>
public sealed class MemTool : ITool
{
    private const int CellCap = 240;

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

    private readonly IUsageSignal _usageSignal;

    public MemTool(IUsageSignal usageSignal) => _usageSignal = usageSignal;

    public string Name => "mem";
    public string CliVerb => "mem";
    public string McpName => "rule";
    public string Help =>
        "mem <text> | mem --hard             recall a migrated RULE/preference (CLI, up to 6 hits; " +
        "--hard lists the always-on core). MCP rule(query): up to 4 hits, reinforces the usage signal.";

    public string ExecuteCli(SqliteConnection connection, string terms, bool hard)
    {
        if (hard)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT type,title,hook FROM memory WHERE hard=1 ORDER BY type,title";
            StringBuilder hardSb = new();
            int hardCount = 0;
            using (SqliteDataReader reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    hardSb.AppendLine($"• [{reader.GetString(0)}] {reader.GetString(1)} — {reader.GetString(2)}");
                    hardCount++;
                }
            }
            hardSb.Append($"({hardCount} hard rule(s) — the always-on core)");
            return hardSb.ToString();
        }

        string match = BuildMatch(terms);
        if (match.Length == 0) return "no usable terms.";

        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT mem.type,mem.hook,mem.body,mem.hard,m.score
            FROM (SELECT k, bm25(memory_fts) AS score FROM memory_fts WHERE memory_fts MATCH $m ORDER BY score LIMIT 6) m
            JOIN memory mem ON mem.k=m.k ORDER BY m.score
            """;
        cmd.Parameters.AddWithValue("$m", match);

        StringBuilder sb = new();
        int n = 0;
        using (SqliteDataReader reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                string body = reader.GetString(2);
                string snip = body.Length <= 300 ? body : body[..300] + "…";
                string tag = reader.GetInt32(3) == 1 ? "HARD " : "";
                sb.AppendLine($"• [{tag}{reader.GetString(0)}] {reader.GetString(1)}\n  {snip}\n");
                n++;
            }
        }
        if (n == 0)
        {
            GapLog.Record(connection, "mem", string.Join(' ', Tokens(terms)));
            return $"no memory matches \"{terms}\" (gap logged).";
        }
        // Each row was appended with AppendLine on top of its own embedded trailing "\n" (aitm.cs's old
        // MemCmd did the same via one Console.WriteLine per row) — CLI dispatch wraps this return value
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
                SELECT mem.type,mem.hook,mem.body,mem.hard,mem.k
                FROM (SELECT k, bm25(memory_fts) AS s FROM memory_fts WHERE memory_fts MATCH $m ORDER BY s LIMIT 4) x
                JOIN memory mem ON mem.k=x.k ORDER BY x.s
                """;
            cmd.Parameters.AddWithValue("$m", match);
            StringBuilder sb = new();
            List<string> hitKeys = [];
            using (SqliteDataReader reader = cmd.ExecuteReader())
            {
                while (reader.Read())
                {
                    string tag = reader.GetInt32(3) == 1 ? "HARD " : "";
                    hitKeys.Add(reader.GetString(4));
                    sb.AppendLine($"• [{tag}{reader.GetString(0)}] {reader.GetString(1)}\n  {Clip(reader.GetString(2), CellCap)}");
                }
            }
            if (sb.Length == 0)
                return $"no rule matches \"{query}\".{McpLogGap(connection, query)}";
            _usageSignal.Reinforce(connection, "memory", hitKeys);
            return OutputBudget.Clip(sb.ToString());
        }
        catch (SqliteException)
        {
            return "memory not indexed yet for this instance (run: aitm index-memory --from <memory dir>).";
        }
    }

    private static string McpLogGap(SqliteConnection connection, string query)
    {
        try
        {
            string norm = string.Join(' ', McpTokens(query));
            if (norm.Length < 3) return "";
            GapLog.Record(connection, "rule", norm);
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
