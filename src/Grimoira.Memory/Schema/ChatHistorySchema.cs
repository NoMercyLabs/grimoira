using Microsoft.Data.Sqlite;

namespace Grimoira.Memory.Schema;

/// <summary>Additive chat metadata for stores created by earlier Grimoira builds.</summary>
public static class ChatHistorySchema
{
    public static void Ensure(SqliteConnection connection)
    {
        using (SqliteCommand create = connection.CreateCommand())
        {
            create.CommandText = "CREATE TABLE IF NOT EXISTS chat(k TEXT PRIMARY KEY, session TEXT, ts TEXT, role TEXT, text TEXT);" +
                "CREATE VIRTUAL TABLE IF NOT EXISTS chat_fts USING fts5(k UNINDEXED, text);";
            create.ExecuteNonQuery();
        }
        HashSet<string> columns = [];
        using (SqliteCommand info = connection.CreateCommand())
        {
            info.CommandText = "PRAGMA table_info(chat)";
            using SqliteDataReader reader = info.ExecuteReader();
            while (reader.Read()) columns.Add(reader.GetString(1));
        }
        using SqliteCommand hasRows = connection.CreateCommand();
        hasRows.CommandText = "SELECT 1 FROM chat LIMIT 1";
        if ((!columns.Contains("kind") || !columns.Contains("sidechain") || !columns.Contains("source_path")) &&
            hasRows.ExecuteScalar() is not null && connection.DataSource is { Length: > 0 } dataSource && File.Exists(dataSource))
        {
            string backupPath = dataSource + ".pre-chat-schema-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss") + ".db";
            using SqliteConnection backup = new($"Data Source={backupPath}");
            backup.Open();
            connection.BackupDatabase(backup);
        }
        foreach ((string name, string definition) in new[]
        {
            ("kind", "TEXT NOT NULL DEFAULT 'human'"),
            ("sidechain", "INTEGER NOT NULL DEFAULT 0"),
            ("source_path", "TEXT"),
        })
        {
            if (columns.Contains(name)) continue;
            using SqliteCommand alter = connection.CreateCommand();
            alter.CommandText = $"ALTER TABLE chat ADD COLUMN {name} {definition}";
            alter.ExecuteNonQuery();
        }
        using SqliteCommand index = connection.CreateCommand();
        index.CommandText = "CREATE INDEX IF NOT EXISTS chat_session_ts_idx ON chat(session,ts,kind);" +
            "CREATE INDEX IF NOT EXISTS chat_duplicate_idx ON chat(session,ts,role)";
        index.ExecuteNonQuery();
        LabelRecognizableLegacyRows(connection);
    }

    private static void LabelRecognizableLegacyRows(SqliteConnection connection)
    {
        using SqliteCommand create = connection.CreateCommand();
        create.CommandText = "CREATE TABLE IF NOT EXISTS chat_history_meta(k TEXT PRIMARY KEY)";
        create.ExecuteNonQuery();
        using SqliteCommand check = connection.CreateCommand();
        check.CommandText = "SELECT 1 FROM chat_history_meta WHERE k='legacy-kinds-v1'";
        if (check.ExecuteScalar() is not null) return;
        using SqliteTransaction transaction = connection.BeginTransaction();
        string[] updates =
        [
            "UPDATE chat SET kind='compaction_summary' WHERE role='user' AND kind='human' AND lower(ltrim(text)) LIKE 'this session is being continued%'",
            "UPDATE chat SET kind='slash_command' WHERE role='user' AND kind='human' AND (lower(ltrim(text)) LIKE '<command-name>/%' OR substr(ltrim(text),1,1)='/')",
            "UPDATE chat SET kind='hook_feedback' WHERE role='user' AND kind='human' AND (lower(ltrim(text)) LIKE 'stop hook feedback:%' OR lower(ltrim(text)) LIKE '<command%')",
            "UPDATE chat SET kind='skill_body' WHERE role='user' AND kind='human' AND (lower(ltrim(text)) LIKE '<skill%' OR lower(ltrim(text)) LIKE '# workflow authoring reference%')",
            "UPDATE chat SET kind='agent_report' WHERE role='user' AND kind='human' AND lower(ltrim(text)) LIKE '<task-notification>%'",
            "UPDATE chat SET kind='system_notice' WHERE role='user' AND kind='human' AND lower(ltrim(text)) LIKE '<system-reminder>%'",
            "UPDATE chat SET kind='loop_prompt' WHERE role='user' AND kind='human' AND lower(ltrim(text)) LIKE 'check %' AND instr(lower(text),'run ')>0",
        ];
        foreach (string sql in updates)
        {
            using SqliteCommand update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = sql;
            update.ExecuteNonQuery();
        }
        using SqliteCommand marker = connection.CreateCommand();
        marker.Transaction = transaction;
        marker.CommandText = "INSERT INTO chat_history_meta(k) VALUES('legacy-kinds-v1')";
        marker.ExecuteNonQuery();
        transaction.Commit();
    }
}
