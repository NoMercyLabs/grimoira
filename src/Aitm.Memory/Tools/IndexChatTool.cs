using System.Text;
using System.Text.Json;
using Aitm.Store.Tools;
using Microsoft.Data.Sqlite;

namespace Aitm.Memory.Tools;

/// <summary>
/// Ingests a Claude Code session transcript (one <c>.jsonl</c> file, or a whole transcript dir) into the
/// chat channel. Copied verbatim from aitm.cs's <c>IndexChat</c>/<c>IndexChatFile</c>/<c>ExtractUserText</c>
/// (aitm.cs:2289, 2298, 2331). Upserts by <c>session:uuid</c>, so re-running the same transcript is
/// idempotent — it re-reads the same rows, it never doubles them.
/// </summary>
public sealed class IndexChatTool : ITool
{
    public string Name => "index-chat";
    public string CliVerb => "index-chat";
    public string? McpName => null;
    public string Help => "index-chat --from <session.jsonl | transcript dir>   ingest a transcript into the chat channel";

    public string Execute(SqliteConnection connection, string fromPath)
    {
        IEnumerable<string> files = Directory.Exists(fromPath)
            ? Directory.EnumerateFiles(fromPath, "*.jsonl")
            : [fromPath];

        StringBuilder sb = new();
        int total = 0;
        foreach (string file in files) total += IndexChatFile(connection, file, sb);
        sb.Append($"done: {total} user message(s) indexed into the chat channel.");
        return sb.ToString();
    }

    private static int IndexChatFile(SqliteConnection connection, string path, StringBuilder sb)
    {
        if (!File.Exists(path)) { sb.AppendLine($"  not found: {path}"); return 0; }
        string session = Path.GetFileNameWithoutExtension(path);
        int stored = 0;
        using (SqliteCommand begin = connection.CreateCommand()) { begin.CommandText = "BEGIN"; begin.ExecuteNonQuery(); }
        foreach (string line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonDocument doc;
            try { doc = JsonDocument.Parse(line); }
            catch { continue; }
            using (doc)
            {
                JsonElement root = doc.RootElement;
                if (!root.TryGetProperty("type", out JsonElement type) || type.GetString() != "user") continue;
                if (!root.TryGetProperty("message", out JsonElement msg)) continue;
                string? text = ExtractUserText(msg)?.Trim();
                if (string.IsNullOrEmpty(text) || text.Length < 40 || text[0] == '<') continue;
                string uuid = root.TryGetProperty("uuid", out JsonElement u) ? u.GetString() ?? "" : "";
                string ts = root.TryGetProperty("timestamp", out JsonElement t) ? t.GetString() ?? "" : "";
                string k = $"{session}:{uuid}";

                using (SqliteCommand upsert = connection.CreateCommand())
                {
                    upsert.CommandText = """
                        INSERT INTO chat(k,session,ts,role,text) VALUES($k,$se,$ts,'user',$tx)
                        ON CONFLICT(k) DO UPDATE SET text=$tx,ts=$ts
                        """;
                    upsert.Parameters.AddWithValue("$k", k);
                    upsert.Parameters.AddWithValue("$se", session);
                    upsert.Parameters.AddWithValue("$ts", ts);
                    upsert.Parameters.AddWithValue("$tx", text);
                    upsert.ExecuteNonQuery();
                }
                using (SqliteCommand del = connection.CreateCommand())
                {
                    del.CommandText = "DELETE FROM chat_fts WHERE k=$k";
                    del.Parameters.AddWithValue("$k", k);
                    del.ExecuteNonQuery();
                }
                using (SqliteCommand ins = connection.CreateCommand())
                {
                    ins.CommandText = "INSERT INTO chat_fts(k,text) VALUES($k,$tx)";
                    ins.Parameters.AddWithValue("$k", k);
                    ins.Parameters.AddWithValue("$tx", text);
                    ins.ExecuteNonQuery();
                }
                stored++;
            }
        }
        using (SqliteCommand commit = connection.CreateCommand()) { commit.CommandText = "COMMIT"; commit.ExecuteNonQuery(); }
        sb.AppendLine($"  {session}: {stored} message(s)");
        return stored;
    }

    // User content is a plain string or an array of blocks; only real text blocks are human input
    // (a content array of tool_result blocks is a tool echo, not something the operator said) -> null.
    private static string? ExtractUserText(JsonElement message)
    {
        if (!message.TryGetProperty("content", out JsonElement content)) return null;
        if (content.ValueKind == JsonValueKind.String) return content.GetString();
        if (content.ValueKind != JsonValueKind.Array) return null;
        StringBuilder sb = new();
        foreach (JsonElement block in content.EnumerateArray())
        {
            if (block.ValueKind == JsonValueKind.Object
                && block.TryGetProperty("type", out JsonElement bt) && bt.GetString() == "text"
                && block.TryGetProperty("text", out JsonElement txt))
                sb.AppendLine(txt.GetString());
        }
        return sb.Length == 0 ? null : sb.ToString();
    }
}
