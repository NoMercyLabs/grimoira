using Microsoft.Data.Sqlite;

namespace Grimoira.Facts.Data;

/// <summary>
/// The <c>facts</c> row snapshot the mutation log stores before/after a write. Copied verbatim from
/// grimoira.cs's <c>ReadFactJson</c> (grimoira.cs:602) — shared by <c>AddTool</c> and <c>ShedFactTool</c>, both
/// of which need the "before" snapshot for the same table.
/// </summary>
public static class FactRecord
{
    public static string? ReadJson(SqliteConnection connection, string key)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT term,aliases,category,value,source,notes FROM facts WHERE k=$k";
        command.Parameters.AddWithValue("$k", key);
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        static string E(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r") + "\"";
        return "{" +
            $"\"term\":{E(reader.GetString(0))},\"aliases\":{E(reader.GetString(1))},\"category\":{E(reader.GetString(2))}," +
            $"\"value\":{E(reader.GetString(3))},\"source\":{E(reader.GetString(4))},\"notes\":{E(reader.GetString(5))}}}";
    }
}
