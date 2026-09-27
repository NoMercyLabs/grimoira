using System.Text;
using Grimora.Store.Data;
using Grimora.Store.Tools;
using Microsoft.Data.Sqlite;

namespace Grimora.Memory.Tools;

/// <summary>
/// Applies <see cref="SecretScrubber"/> to rows already stored in <c>chat</c> (docs/RESTRUCTURE.md,
/// design checklist "Secrets in outputs"). Makes a <c>VACUUM INTO</c> backup first, so a store scrubbed
/// by mistake can be recovered, then reports how many rows were touched and how many of each kind of
/// secret were found. <c>--dry-run</c> runs the same scan and report without writing anything back.
/// </summary>
public sealed class RedactChatTool : ITool
{
    public string Name => "redact-chat";
    public string CliVerb => "redact-chat";
    public string? McpName => null;
    public string Help => "redact-chat [--dry-run]               scrub token-shaped strings already stored in chat (backs up first)";

    public string Execute(SqliteConnection connection, string root, bool dryRun)
    {
        string backupDir = Path.Combine(root, "backups");
        Directory.CreateDirectory(backupDir);
        string backupPath = Path.Combine(backupDir, $"grimora-pre-redact-chat-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}.db");
        using (SqliteCommand backup = connection.CreateCommand())
        {
            backup.CommandText = "VACUUM INTO $path";
            backup.Parameters.AddWithValue("$path", backupPath);
            backup.ExecuteNonQuery();
        }

        List<(string Key, string Text)> rows = [];
        using (SqliteCommand select = connection.CreateCommand())
        {
            select.CommandText = "SELECT k, text FROM chat";
            using SqliteDataReader reader = select.ExecuteReader();
            while (reader.Read()) rows.Add((reader.GetString(0), reader.GetString(1)));
        }

        Dictionary<string, int> totalCounts = [];
        int changed = 0;
        foreach ((string key, string text) in rows)
        {
            (string scrubbed, IReadOnlyDictionary<string, int> counts) = SecretScrubber.Redact(text);
            if (counts.Count == 0) continue;
            changed++;
            foreach ((string kind, int n) in counts) totalCounts[kind] = totalCounts.GetValueOrDefault(kind) + n;
            if (dryRun) continue;

            using (SqliteCommand update = connection.CreateCommand())
            {
                update.CommandText = "UPDATE chat SET text=$tx WHERE k=$k";
                update.Parameters.AddWithValue("$tx", scrubbed);
                update.Parameters.AddWithValue("$k", key);
                update.ExecuteNonQuery();
            }
            using (SqliteCommand delFts = connection.CreateCommand())
            {
                delFts.CommandText = "DELETE FROM chat_fts WHERE k=$k";
                delFts.Parameters.AddWithValue("$k", key);
                delFts.ExecuteNonQuery();
            }
            using (SqliteCommand insFts = connection.CreateCommand())
            {
                insFts.CommandText = "INSERT INTO chat_fts(k,text) VALUES($k,$tx)";
                insFts.Parameters.AddWithValue("$k", key);
                insFts.Parameters.AddWithValue("$tx", scrubbed);
                insFts.ExecuteNonQuery();
            }
        }

        StringBuilder sb = new();
        sb.AppendLine($"backup -> {backupPath}");
        sb.AppendLine(dryRun ? $"dry run: {changed} row(s) would be redacted" : $"redacted {changed} row(s)");
        foreach ((string kind, int n) in totalCounts.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            sb.AppendLine($"  {kind}: {n}");
        return sb.ToString().TrimEnd();
    }
}
