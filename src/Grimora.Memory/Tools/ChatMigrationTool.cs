using Grimora.Memory.Schema;
using Microsoft.Data.Sqlite;

namespace Grimora.Memory.Tools;

/// <summary>Compares old chat rows by key and imports only rows absent from the current store.</summary>
public sealed class ChatMigrationTool
{
    public string Compare(SqliteConnection current, string oldPath)
    {
        ChatHistorySchema.Ensure(current);
        using SqliteConnection old = OpenOld(oldPath);
        long total = 0, missing = 0, differing = 0;
        using SqliteCommand rows = old.CreateCommand();
        rows.CommandText = "SELECT k,session,ts,role,text FROM chat ORDER BY k";
        using SqliteDataReader reader = rows.ExecuteReader();
        while (reader.Read())
        {
            total++;
            using SqliteCommand check = current.CreateCommand();
            check.CommandText = "SELECT session,ts,role,text FROM chat WHERE k=$k";
            check.Parameters.AddWithValue("$k", reader.GetString(0));
            using SqliteDataReader present = check.ExecuteReader();
            if (!present.Read()) { missing++; continue; }
            for (int i = 0; i < 4; i++)
                if (!string.Equals(reader.IsDBNull(i + 1) ? "" : reader.GetString(i + 1),
                    present.IsDBNull(i) ? "" : present.GetString(i), StringComparison.Ordinal))
                { differing++; break; }
        }
        return $"old {total}; missing {missing}; differing {differing}";
    }

    public string ImportMissing(SqliteConnection current, string oldPath)
    {
        ChatHistorySchema.Ensure(current);
        string backupPath = current.DataSource + ".pre-chat-import-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss") + ".db";
        using (SqliteConnection backup = new($"Data Source={backupPath}"))
        {
            backup.Open();
            current.BackupDatabase(backup);
        }
        using SqliteConnection old = OpenOld(oldPath);
        long imported = 0;
        using SqliteTransaction transaction = current.BeginTransaction();
        using SqliteCommand rows = old.CreateCommand();
        rows.CommandText = "SELECT k,session,ts,role,text FROM chat ORDER BY k";
        using SqliteDataReader reader = rows.ExecuteReader();
        while (reader.Read())
        {
            string key = reader.GetString(0);
            using SqliteCommand insert = current.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT OR IGNORE INTO chat(k,session,ts,role,text,kind) VALUES($k,$s,$t,$r,$x,'human')";
            insert.Parameters.AddWithValue("$k", key);
            insert.Parameters.AddWithValue("$s", reader.IsDBNull(1) ? "" : reader.GetString(1));
            insert.Parameters.AddWithValue("$t", reader.IsDBNull(2) ? "" : reader.GetString(2));
            insert.Parameters.AddWithValue("$r", reader.IsDBNull(3) ? "user" : reader.GetString(3));
            string text = reader.IsDBNull(4) ? "" : reader.GetString(4);
            insert.Parameters.AddWithValue("$x", text);
            if (insert.ExecuteNonQuery() == 0) continue;
            using SqliteCommand fts = current.CreateCommand();
            fts.Transaction = transaction;
            fts.CommandText = "INSERT INTO chat_fts(k,text) VALUES($k,$x)";
            fts.Parameters.AddWithValue("$k", key);
            fts.Parameters.AddWithValue("$x", text);
            fts.ExecuteNonQuery();
            imported++;
        }
        transaction.Commit();
        return $"imported {imported}; backup {backupPath}; {Compare(current, oldPath)}";
    }

    private static SqliteConnection OpenOld(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("old chat store not found", path);
        SqliteConnection old = new($"Data Source={path};Mode=ReadOnly");
        old.Open();
        return old;
    }
}
