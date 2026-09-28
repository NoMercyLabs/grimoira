using System.Text;
using Grimora.Store.Data;
using Grimora.Store.Tools;
using Grimora.Memory.Schema;
using Microsoft.Data.Sqlite;

namespace Grimora.Memory.Tools;

/// <summary>
/// Recall channel: search past conversations, kept separate from the verified-facts query channel. CLI
/// verb <c>recall</c> and MCP tool <c>recall</c> are one job with two shapes today (RESTRUCTURE.md
/// section 2.2): <c>ExecuteCli</c> is copied verbatim from grimora.cs's <c>RecallCmd</c> (grimora.cs:2352),
/// <c>ExecuteMcp</c> from mcp.cs's <c>recall</c> (mcp.cs:452) — neither reinforces the usage signal
/// (chat is not ref-bridged to the brain graph the way facts/memory are).
/// </summary>
public sealed class RecallTool : ITool
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

    public string Name => "recall";
    public string CliVerb => "recall";
    public string McpName => "recall";
    public string Help =>
        "recall <text>                        search past conversations (CLI: up to 5 hits, with score). " +
        "MCP recall(query): up to 4 hits.";

    public string ExecuteCli(SqliteConnection connection, string terms, string kind = "")
    {
        ChatHistorySchema.Ensure(connection);
        string match = BuildMatch(terms);
        if (match.Length == 0) return "no usable terms.";

        string filter = KindFilter(kind);
        long total = CountMatches(connection, match, filter, kind);

        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT ch.session,ch.ts,ch.text,bm25(chat_fts) AS score
            FROM chat_fts JOIN chat ch ON ch.k=chat_fts.k
            WHERE chat_fts MATCH $m {filter}
            ORDER BY score LIMIT 5
            """;
        cmd.Parameters.AddWithValue("$m", match);
        if (kind.Length > 0 && kind != "all") cmd.Parameters.AddWithValue("$kind", kind);

        StringBuilder sb = new();
        int n = 0;
        using (SqliteDataReader reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                string session = reader.GetString(0);
                string text = reader.GetString(2);
                string snip = text.Length <= 240 ? text : text[..240] + "…";
                sb.AppendLine($"• [{session[..Math.Min(8, session.Length)]}… {reader.GetString(1)}]  (score {reader.GetDouble(3):F2})\n  {snip}\n");
                n++;
            }
        }
        if (n == 0) return $"0 total chat matches for \"{terms}\".";
        // Each row was appended with AppendLine on top of its own embedded trailing "\n" (grimora.cs's old
        // RecallCmd did the same via one Console.WriteLine per row) — CLI dispatch wraps this return
        // value in one more Console.WriteLine, so the AppendLine terminator on the LAST row would double
        // up into an extra blank line the old CLI never had. Strip exactly that one terminator; the
        // embedded "\n" stays, so the final Console.WriteLine reproduces the old row's own line ending.
        return $"{total} total; showing {n}; remaining {Math.Max(0, total - n)}\n" + StripOneTrailingNewLine(sb.ToString());
    }

    public string ExecuteMcp(SqliteConnection connection, string query, string kind = "")
    {
        ChatHistorySchema.Ensure(connection);
        string match = McpMatch(query);
        if (match.Length == 0) return "no usable query terms.";
        try
        {
            string filter = KindFilter(kind);
            long total = CountMatches(connection, match, filter, kind);
            using SqliteCommand cmd = connection.CreateCommand();
            cmd.CommandText = $"""
                SELECT ch.ts,ch.text,bm25(chat_fts) AS s
                FROM chat_fts JOIN chat ch ON ch.k=chat_fts.k
                WHERE chat_fts MATCH $m {filter}
                ORDER BY s LIMIT 4
                """;
            cmd.Parameters.AddWithValue("$m", match);
            if (kind.Length > 0 && kind != "all") cmd.Parameters.AddWithValue("$kind", kind);
            StringBuilder sb = new();
            int shown = 0;
            using (SqliteDataReader reader = cmd.ExecuteReader())
            {
                while (reader.Read()) { sb.AppendLine($"• [{reader.GetString(0)}] {Clip(reader.GetString(1), CellCap)}"); shown++; }
            }
            return sb.Length == 0 ? $"0 total chat matches for \"{query}\".{McpLogGap(connection, query)}" :
                $"{total} total; showing {shown}; remaining {Math.Max(0, total - shown)}\n" + OutputBudget.Clip(sb.ToString());
        }
        catch (SqliteException)
        {
            return "chat history not indexed yet for this instance (run: grimora index-chat --from <transcript dir>).";
        }
    }

    private static string KindFilter(string kind) => kind switch
    {
        "" => "AND ch.kind IN ('human','slash_command')",
        "all" => "",
        "human" or "assistant" or "compaction_summary" or "hook_feedback" or "skill_body" or "agent_report" or
            "system_notice" or "loop_prompt" or "slash_command" or "tool_result_doc" => "AND ch.kind=$kind",
        _ => throw new ArgumentException("unknown chat kind", nameof(kind)),
    };

    private static long CountMatches(SqliteConnection connection, string match, string filter, string kind)
    {
        using SqliteCommand count = connection.CreateCommand();
        count.CommandText = $"SELECT count(*) FROM chat_fts JOIN chat ch ON ch.k=chat_fts.k WHERE chat_fts MATCH $m {filter}";
        count.Parameters.AddWithValue("$m", match);
        if (kind.Length > 0 && kind != "all") count.Parameters.AddWithValue("$kind", kind);
        return (long)(count.ExecuteScalar() ?? 0L);
    }

    private static string McpLogGap(SqliteConnection connection, string query)
    {
        try
        {
            string norm = string.Join(' ', McpTokens(query));
            if (norm.Length < 3) return "";
            GapLog.Record(connection, "recall", norm);
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
