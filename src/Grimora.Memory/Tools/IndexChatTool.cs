using System.Security.Cryptography;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Grimora.Memory.Data;
using Grimora.Memory.Schema;
using Grimora.Store.Data;
using Grimora.Store.Tools;
using Microsoft.Data.Sqlite;
using static Grimora.Store.Data.JsonShape;

namespace Grimora.Memory.Tools;

/// <summary>Streams Claude transcript turns into the chat channel. Replaying a file updates its own rows.</summary>
public sealed class IndexChatTool : ITool
{
    public string Name => "index-chat";
    public string CliVerb => "index-chat";
    public string? McpName => null;
    public string Help => "index-chat --from <session.jsonl | transcript dir>   ingest all transcript turns";

    public string Execute(SqliteConnection connection, string fromPath)
    {
        ChatHistorySchema.Ensure(connection);
        IEnumerable<string> files = Directory.Exists(fromPath)
            ? Directory.EnumerateFiles(fromPath, "*.jsonl", SearchOption.AllDirectories)
            : [fromPath];
        StringBuilder sb = new();
        int total = 0;
        foreach (string file in files) total += IndexChatFile(connection, file, sb);
        sb.Append($"done: {total} turn(s) indexed into the chat channel.");
        return sb.ToString();
    }

    private static int IndexChatFile(SqliteConnection connection, string path, StringBuilder sb)
    {
        if (!File.Exists(path)) { sb.AppendLine($"  not found: {path}"); return 0; }
        string session = Path.GetFileNameWithoutExtension(path);
        int stored = 0;
        Dictionary<string, string> readPaths = new(StringComparer.Ordinal);
        Dictionary<string, bool> trackedPaths = new(StringComparer.OrdinalIgnoreCase);
        using SqliteTransaction transaction = connection.BeginTransaction();
        foreach (string line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonDocument doc;
            try { doc = JsonDocument.Parse(line); }
            catch (JsonException) { continue; }
            using (doc)
            {
                JsonElement root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) continue;
                string type = GetString(root, "type") ?? "";
                if (type is not ("user" or "assistant")) continue;
                if (!TryGetObjectProperty(root, "message", out JsonElement message) || message.ValueKind != JsonValueKind.Object) continue;
                if (TryGetObjectProperty(message, "content", out JsonElement blocks) && blocks.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement block in blocks.EnumerateArray())
                    {
                        if (block.ValueKind != JsonValueKind.Object) continue;
                        if (type == "assistant" && GetString(block, "type") == "tool_use" && GetString(block, "name") == "Read" &&
                            GetString(block, "id") is { } id && TryGetObjectProperty(block, "input", out JsonElement input) &&
                            GetString(input, "file_path") is { } filePath)
                            readPaths[id] = filePath;
                        if (type == "user" && GetString(block, "type") == "tool_result" &&
                            GetString(block, "tool_use_id") is { } resultId && readPaths.TryGetValue(resultId, out string? sourcePath) &&
                            GetString(block, "content") is { Length: > 0 } resultText &&
                            ShouldKeepRead(sourcePath, GetString(root, "cwd"), trackedPaths))
                        {
                            string docKey = session + ":" + (GetString(root, "uuid") ?? resultId) + ":doc:" + resultId;
                            Store(connection, transaction, docKey, session, GetString(root, "timestamp") ?? "", "tool",
                                "tool_result_doc", resultText, sourcePath,
                                IsTrue(root, "isSidechain"));
                            stored++;
                        }
                    }
                }
                // A user turn reads through the one transcript recogniser so a pasted document block is kept,
                // ahead of the typed text, exactly as the PreCompact ledger keeps it.
                string? text = type == "user" ? TranscriptClassifier.ExtractUserText(root) : ExtractText(message);
                if (string.IsNullOrWhiteSpace(text)) continue;
                text = text.Trim();
                string role = type;
                string kind = Classify(root, role, text);
                if (role == "user" && PastedContent(text) is { } pasted)
                {
                    string pastedKey = session + ":" + (GetString(root, "uuid") ?? "") + ":pasted";
                    Store(connection, transaction, pastedKey, session, GetString(root, "timestamp") ?? "", "tool",
                        "tool_result_doc", pasted, "pasted_content", false);
                    stored++;
                }
                (text, _) = SecretScrubber.Redact(text);
                string ts = GetString(root, "timestamp") ?? "";
                string uuid = GetString(root, "uuid") ?? "";
                string k = session + ":" + (uuid.Length > 0 ? uuid : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(role + "\0" + ts + "\0" + text))));
                if (IsDuplicate(connection, transaction, k, session, ts, role, text)) continue;
                Store(connection, transaction, k, session, ts, role, kind, text, null,
                    IsTrue(root, "isSidechain"));
                stored++;
            }
        }
        stored += StoreClassifiedEntries(connection, transaction, path, session);
        transaction.Commit();
        sb.AppendLine($"  {session}: {stored} turn(s)");
        return stored;
    }

    /// <summary>The owner's words the streaming pass above cannot see: an AskUserQuestion answer lives in
    /// <c>toolUseResult.answers</c> on a turn whose only block is a tool_result, and a mid-turn message is a
    /// <c>type: attachment</c> entry, not a user turn. Both come from the one transcript recogniser the
    /// PreCompact ledger uses (<see cref="TranscriptClassifier"/>), so the chat index and the ledger agree
    /// on what was said. Peers' queued messages are kept as agent_report, never as the owner's own words.</summary>
    private static int StoreClassifiedEntries(SqliteConnection connection, SqliteTransaction transaction, string path, string session)
    {
        int stored = 0;
        (List<TranscriptClassifier.ClassifiedEntry> entries, _) = TranscriptClassifier.ClassifyEntries(TranscriptClassifier.ReadEntries(path));
        foreach (TranscriptClassifier.ClassifiedEntry entry in entries)
        {
            bool isPeer = entry.Who.StartsWith("Peer ", StringComparison.Ordinal);
            if (!isPeer && entry.Who is not ("the owner (answer)" or "the owner (mid-turn)")) continue;
            string text = isPeer ? $"{entry.Who}: {entry.Text}" : entry.Text;
            if (string.IsNullOrWhiteSpace(text)) continue;
            (text, _) = SecretScrubber.Redact(text);
            string kind = isPeer ? "agent_report" : "human";
            string k = session + ":" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(entry.Who + "\0" + entry.Timestamp + "\0" + text)));
            if (IsDuplicate(connection, transaction, k, session, entry.Timestamp, "user", text)) continue;
            Store(connection, transaction, k, session, entry.Timestamp, "user", kind, text, null, false);
            stored++;
        }
        return stored;
    }

    private static bool IsDuplicate(SqliteConnection connection, SqliteTransaction transaction, string key,
        string session, string ts, string role, string text)
    {
        using SqliteCommand find = connection.CreateCommand();
        find.Transaction = transaction;
        find.CommandText = "SELECT k FROM chat WHERE session=$session AND ts=$ts AND role=$role AND text=$text LIMIT 1";
        find.Parameters.AddWithValue("$session", session);
        find.Parameters.AddWithValue("$ts", ts);
        find.Parameters.AddWithValue("$role", role);
        find.Parameters.AddWithValue("$text", text);
        return find.ExecuteScalar() is string existing && existing != key;
    }

    private static void Store(SqliteConnection connection, SqliteTransaction transaction, string k, string session,
        string ts, string role, string kind, string text, string? sourcePath, bool sidechain)
    {
        (text, _) = SecretScrubber.Redact(text);
        using SqliteCommand previous = connection.CreateCommand();
        previous.Transaction = transaction;
        previous.CommandText = "SELECT text FROM chat WHERE k=$k";
        previous.Parameters.AddWithValue("$k", k);
        string? oldText = previous.ExecuteScalar() as string;
        using (SqliteCommand upsert = connection.CreateCommand())
        {
            upsert.Transaction = transaction;
            upsert.CommandText = """
                INSERT INTO chat(k,session,ts,role,text,kind,sidechain,source_path)
                VALUES($k,$se,$ts,$ro,$tx,$ki,$sc,$path)
                ON CONFLICT(k) DO UPDATE SET ts=excluded.ts,role=excluded.role,text=excluded.text,
                    kind=excluded.kind,sidechain=excluded.sidechain,source_path=excluded.source_path
                """;
            upsert.Parameters.AddWithValue("$k", k);
            upsert.Parameters.AddWithValue("$se", session);
            upsert.Parameters.AddWithValue("$ts", ts);
            upsert.Parameters.AddWithValue("$ro", role);
            upsert.Parameters.AddWithValue("$tx", text);
            upsert.Parameters.AddWithValue("$ki", kind);
            upsert.Parameters.AddWithValue("$sc", sidechain ? 1 : 0);
            upsert.Parameters.AddWithValue("$path", (object?)sourcePath ?? DBNull.Value);
            upsert.ExecuteNonQuery();
        }
        if (oldText == text) return;
        using (SqliteCommand fts = connection.CreateCommand())
        {
            fts.Transaction = transaction;
            fts.CommandText = oldText is null
                ? "INSERT INTO chat_fts(k,text) VALUES($k,$tx)"
                : "DELETE FROM chat_fts WHERE k=$k; INSERT INTO chat_fts(k,text) VALUES($k,$tx)";
            fts.Parameters.AddWithValue("$k", k);
            fts.Parameters.AddWithValue("$tx", text);
            fts.ExecuteNonQuery();
        }
    }

    private static bool ShouldKeepRead(string path, string? cwd, Dictionary<string, bool> cache)
    {
        if (cache.TryGetValue(path, out bool tracked)) return !tracked;
        string directory = Path.GetDirectoryName(path) ?? cwd ?? Directory.GetCurrentDirectory();
        if (!Directory.Exists(directory)) directory = cwd ?? Directory.GetCurrentDirectory();
        try
        {
            using Process process = new();
            process.StartInfo = new ProcessStartInfo("git")
            {
                WorkingDirectory = directory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            process.StartInfo.ArgumentList.Add("ls-files");
            process.StartInfo.ArgumentList.Add("--error-unmatch");
            process.StartInfo.ArgumentList.Add("--");
            process.StartInfo.ArgumentList.Add(path);
            process.Start();
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(5000))
            {
                process.Kill(entireProcessTree: true);
                tracked = true;
            }
            else
            {
                Task.WhenAll(stdout, stderr).GetAwaiter().GetResult();
                tracked = process.ExitCode == 0;
            }
        }
        catch { tracked = true; }
        cache[path] = tracked;
        return !tracked;
    }

    private static string Classify(JsonElement root, string role, string text)
    {
        if (role == "assistant") return "assistant";
        string start = text.TrimStart();
        if (IsTrue(root, "isCompactSummary") ||
            start.StartsWith("This session is being continued", StringComparison.OrdinalIgnoreCase)) return "compaction_summary";
        if (start.StartsWith("<command-name>/", StringComparison.OrdinalIgnoreCase)) return "slash_command";
        if (start.StartsWith("Stop hook feedback:", StringComparison.OrdinalIgnoreCase) ||
            start.StartsWith("<command", StringComparison.OrdinalIgnoreCase)) return "hook_feedback";
        if (TryGetObjectProperty(root, "attributionSkill", out _) || start.StartsWith("<skill", StringComparison.OrdinalIgnoreCase) ||
            start.StartsWith("# Workflow authoring reference", StringComparison.OrdinalIgnoreCase)) return "skill_body";
        if (start.StartsWith("<task-notification>", StringComparison.OrdinalIgnoreCase)) return "agent_report";
        if (IsTrue(root, "isMeta") ||
            GetString(root, "promptSource") == "system" || start.StartsWith("<system-reminder>", StringComparison.OrdinalIgnoreCase))
            return "system_notice";
        if (IsTrue(root, "isSidechain")) return "agent_report";
        if (start.StartsWith("Check ", StringComparison.OrdinalIgnoreCase) && start.Contains("run ", StringComparison.OrdinalIgnoreCase)) return "loop_prompt";
        if (start.StartsWith('/') || PastedContent(start) is { } pasted &&
            (pasted.TrimStart().StartsWith('/') || pasted.Contains("\n/goal ", StringComparison.OrdinalIgnoreCase))) return "slash_command";
        return "human";
    }

    private static string? PastedContent(string text)
    {
        int open = text.IndexOf("<pasted_content", StringComparison.OrdinalIgnoreCase);
        if (open < 0) return null;
        int begin = text.IndexOf('>', open);
        if (begin < 0) return null;
        int end = text.IndexOf("</pasted_content", begin + 1, StringComparison.OrdinalIgnoreCase);
        if (end < 0 || text.IndexOf('>', end) < 0) return null;
        return text[(begin + 1)..end].Trim();
    }

    private static string? ExtractText(JsonElement message)
    {
        if (!TryGetObjectProperty(message, "content", out JsonElement content)) return null;
        if (content.ValueKind == JsonValueKind.String) return content.GetString();
        if (content.ValueKind != JsonValueKind.Array) return null;
        StringBuilder sb = new();
        foreach (JsonElement block in content.EnumerateArray())
        {
            if (block.ValueKind == JsonValueKind.Object && GetString(block, "type") == "text" && GetString(block, "text") is { } text)
                sb.AppendLine(text);
        }
        return sb.Length == 0 ? null : sb.ToString();
    }
}
