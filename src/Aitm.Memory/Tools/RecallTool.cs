using System.Text;
using Aitm.Store.Data;
using Aitm.Store.Tools;
using Microsoft.Data.Sqlite;

namespace Aitm.Memory.Tools;

/// <summary>
/// Recall channel: search past conversations, kept separate from the verified-facts query channel. CLI
/// verb <c>recall</c> and MCP tool <c>recall</c> are one job with two shapes today (RESTRUCTURE.md
/// section 2.2): <c>ExecuteCli</c> is copied verbatim from aitm.cs's <c>RecallCmd</c> (aitm.cs:2352),
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

    public string Name => "recall";
    public string CliVerb => "recall";
    public string? McpName => "recall";
    public string Help =>
        "recall <text>                        search past conversations (CLI: up to 5 hits, with score). " +
        "MCP recall(query): up to 4 hits.";

    public string ExecuteCli(SqliteConnection connection, string terms)
    {
        string match = BuildMatch(terms);
        if (match.Length == 0) return "no usable terms.";

        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT ch.session,ch.ts,ch.text,m.score
            FROM (SELECT k, bm25(chat_fts) AS score FROM chat_fts WHERE chat_fts MATCH $m ORDER BY score LIMIT 5) m
            JOIN chat ch ON ch.k=m.k ORDER BY m.score
            """;
        cmd.Parameters.AddWithValue("$m", match);

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
        if (n == 0) return $"no chat history matches \"{terms}\".";
        return sb.ToString();
    }

    public string ExecuteMcp(SqliteConnection connection, string query)
    {
        string match = Match(query);
        if (match.Length == 0) return "no usable query terms.";
        try
        {
            using SqliteCommand cmd = connection.CreateCommand();
            cmd.CommandText = """
                SELECT ch.ts,ch.text
                FROM (SELECT k, bm25(chat_fts) AS s FROM chat_fts WHERE chat_fts MATCH $m ORDER BY s LIMIT 4) x
                JOIN chat ch ON ch.k=x.k ORDER BY x.s
                """;
            cmd.Parameters.AddWithValue("$m", match);
            StringBuilder sb = new();
            using (SqliteDataReader reader = cmd.ExecuteReader())
            {
                while (reader.Read()) sb.AppendLine($"• [{reader.GetString(0)}] {Clip(reader.GetString(1), CellCap)}");
            }
            return sb.Length == 0 ? $"no chat history matches \"{query}\".{McpLogGap(connection, query)}" : OutputBudget.Clip(sb.ToString());
        }
        catch (SqliteException)
        {
            return "chat history not indexed yet for this instance (run: aitm index-chat --from <transcript dir>).";
        }
    }

    private static string McpLogGap(SqliteConnection connection, string query)
    {
        try
        {
            string norm = string.Join(' ', Tokens(query));
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

    private static string BuildMatch(string terms) => string.Join(" OR ", Tokens(terms).Select(t => $"\"{t}\""));

    private static string Match(string terms) => string.Join(" OR ", Tokens(terms).Select(t => $"\"{t}\""));

    private static List<string> Tokens(string terms) =>
        [.. terms.ToLowerInvariant()
            .Split(" \t\r\n-_./\\,;:()[]{}<>\"'`|!?*+=&#@~%$^".ToCharArray(), StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.All(char.IsLetterOrDigit) && t.Length > 1 && !Stop.Contains(t))
            .Distinct()];
}
