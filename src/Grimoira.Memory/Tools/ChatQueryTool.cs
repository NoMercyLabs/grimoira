using System.Text;
using Grimoira.Memory.Schema;
using Grimoira.Store.Tools;
using Microsoft.Data.Sqlite;

namespace Grimoira.Memory.Tools;

/// <summary>Enumerates chat rows with an explicit population count and deterministic paging.</summary>
public sealed class ChatQueryTool : ITool
{
    public string Name => "chat-list";
    public string CliVerb => "chat";
    public string McpName => "chat_list";
    public string Help => "List transcript turns by session, kind and time; each page states total and remaining.";

    public string ExecuteMcp(SqliteConnection connection, string targetSession = "", string kind = "", string from = "", string to = "", int page = 1, int pageSize = 50, bool full = false, string path = "", string command = "") =>
        List(connection, targetSession, kind, from, to, page, pageSize, full, "", path, command);

    public string List(SqliteConnection connection, string targetSession, string kind, string from, string to, int page, int pageSize, bool full, string match = "", string path = "", string command = "")
    {
        ChatHistorySchema.Ensure(connection);
        if (page < 1 || pageSize is < 1 or > 500) return "page must be >= 1 and page size 1..500.";
        if (!ValidKind(kind)) return "unknown chat kind.";
        string where = Where(targetSession, kind, from, to, match, path, command);
        using SqliteCommand count = Build(connection, "SELECT count(*) FROM chat c " + Join(match) + where, targetSession, kind, from, to, match, path, command);
        long total = (long)(count.ExecuteScalar() ?? 0L);
        long offset = (long)(page - 1) * pageSize;
        using SqliteCommand select = Build(connection,
            "SELECT c.k,c.ts,c.kind,c.text,c.source_path FROM chat c " + Join(match) + where +
            " ORDER BY c.ts,c.rowid LIMIT $limit OFFSET $offset", targetSession, kind, from, to, match, path, command);
        select.Parameters.AddWithValue("$limit", pageSize);
        select.Parameters.AddWithValue("$offset", offset);
        StringBuilder sb = new();
        int shown = 0;
        using (SqliteDataReader reader = select.ExecuteReader())
        {
            while (reader.Read())
            {
                string text = reader.GetString(3);
                if (!full)
                {
                    text = text.Replace('\r', ' ').Replace('\n', ' ');
                    if (text.Length > 200) text = text[..200] + "…";
                }
                sb.Append(reader.GetString(0)).Append(' ').Append(reader.GetString(1)).Append(' ')
                    .Append(reader.GetString(2));
                if (!reader.IsDBNull(4)) sb.Append(" path=").Append(reader.GetString(4));
                sb.AppendLine().AppendLine(text);
                shown++;
            }
        }
        long remaining = Math.Max(0, total - offset - shown);
        return $"total {total}; page {page}; shown {shown}; remaining {remaining}\n" + sb;
    }

    public string First(SqliteConnection connection, string targetSession)
    {
        ChatHistorySchema.Ensure(connection);
        if (string.IsNullOrWhiteSpace(targetSession)) return "chat first needs --session <id>.";
        using SqliteCommand count = connection.CreateCommand();
        count.CommandText = "SELECT count(*) FROM chat WHERE session=$session AND role='user' AND kind IN ('human','slash_command')";
        count.Parameters.AddWithValue("$session", targetSession);
        long total = (long)(count.ExecuteScalar() ?? 0L);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT k,ts,kind,text FROM chat WHERE session=$session AND role='user' " +
            "AND kind IN ('human','slash_command') ORDER BY ts,rowid LIMIT 1";
        command.Parameters.AddWithValue("$session", targetSession);
        using SqliteDataReader reader = command.ExecuteReader();
        return reader.Read() ? $"total {total}; shown 1; remaining {total - 1}\n{reader.GetString(0)} {reader.GetString(1)} {reader.GetString(2)}\n{reader.GetString(3)}" :
            "total 0; shown 0; remaining 0";
    }

    public string Commands(SqliteConnection connection, string targetSession = "", int page = 1, int pageSize = 50, bool full = false, string command = "") =>
        List(connection, targetSession, "slash_command", "", "", page, pageSize, full, command: command);

    public string Count(SqliteConnection connection, string match, string kind = "human", string by = "", string targetSession = "")
    {
        ChatHistorySchema.Ensure(connection);
        if (!ValidKind(kind)) return "unknown chat kind.";
        if (by is not ("" or "day" or "session" or "month")) return "group by day, month, or session.";
        string group = by switch { "day" => "substr(c.ts,1,10)", "month" => "substr(c.ts,1,7)", "session" => "c.session", _ => "''" };
        string where = Where(targetSession, kind, "", "", match, "", "");
        using SqliteCommand command = Build(connection,
            $"SELECT {group},count(*) FROM chat c " + Join(match) + where + $" GROUP BY {group} ORDER BY {group}",
            targetSession, kind, "", "", match, "", "");
        StringBuilder sb = new();
        long total = 0;
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            string key = reader.GetString(0);
            long n = reader.GetInt64(1);
            total += n;
            if (by.Length > 0) sb.AppendLine($"{key}: {n}");
        }
        return $"total {total}\n" + sb;
    }

    private static bool ValidKind(string kind) => kind is "" or "human" or "assistant" or "compaction_summary" or
        "hook_feedback" or "skill_body" or "agent_report" or "system_notice" or "loop_prompt" or "slash_command" or "tool_result_doc";

    private static string Join(string match) => match.Length == 0 ? "" : "JOIN chat_fts f ON f.k=c.k ";

    private static string Where(string targetSession, string kind, string from, string to, string match, string path, string command)
    {
        List<string> conditions = [];
        if (targetSession.Length > 0) conditions.Add("c.session=$session");
        if (kind == "human") conditions.Add("c.kind IN ('human','slash_command')");
        else if (kind.Length > 0) conditions.Add("c.kind=$kind");
        if (from.Length > 0) conditions.Add("c.ts >= $from");
        if (to.Length > 0) conditions.Add(to.Length == 10 ? "substr(c.ts,1,10) <= $to" : "c.ts <= $to");
        if (match.Length > 0) conditions.Add("f.text MATCH $match");
        if (path.Length > 0) conditions.Add("c.source_path LIKE '%' || $path || '%'");
        if (command.Length > 0) conditions.Add("(lower(ltrim(c.text)) LIKE '<command-name>' || lower($command) || '<%' " +
            "OR lower(ltrim(c.text)) LIKE lower($command) || ' %' " +
            "OR lower(c.text) LIKE '%' || char(10) || lower($command) || ' %')");
        return conditions.Count == 0 ? "" : "WHERE " + string.Join(" AND ", conditions);
    }

    private static SqliteCommand Build(SqliteConnection connection, string sql, string targetSession, string kind, string from, string to, string match, string path, string commandName)
    {
        SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        if (targetSession.Length > 0) command.Parameters.AddWithValue("$session", targetSession);
        if (kind.Length > 0 && kind != "human") command.Parameters.AddWithValue("$kind", kind);
        if (from.Length > 0) command.Parameters.AddWithValue("$from", from);
        if (to.Length > 0) command.Parameters.AddWithValue("$to", to);
        if (match.Length > 0) command.Parameters.AddWithValue("$match", match);
        if (path.Length > 0) command.Parameters.AddWithValue("$path", path);
        if (commandName.Length > 0) command.Parameters.AddWithValue("$command", commandName);
        return command;
    }
}

public sealed class ChatCountTool : ITool
{
    public string Name => "chat-count";
    public string CliVerb => "chat-count";
    public string McpName => "chat_count";
    public string Help => "Count transcript matches, optionally grouped by day, month, or session.";

    public string ExecuteMcp(SqliteConnection connection, string match, string kind = "human", string by = "", string targetSession = "") =>
        new ChatQueryTool().Count(connection, match, kind, by, targetSession);
}
