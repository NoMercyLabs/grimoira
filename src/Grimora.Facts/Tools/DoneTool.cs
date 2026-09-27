using Grimora.Store.Data;
using Grimora.Store.Tools;
using Microsoft.Data.Sqlite;

namespace Grimora.Facts.Tools;

/// <summary>Closes one open todo by id. Copied verbatim from grimora.cs's <c>CloseTodo</c> (grimora.cs:650).
/// Selftest already covers this verb against today's code (RESTRUCTURE.md slice 6), so no new pinned
/// test is added here.</summary>
public sealed class DoneTool : ITool
{
    public string Name => "done";
    public string CliVerb => "done";
    public string? McpName => null;
    public string Help => "done <id>                             close an open todo by id";

    public string Execute(SqliteConnection connection, int id)
    {
        string? title = Scalar(connection, "SELECT title FROM todos WHERE id=$i AND status='open'", id);
        if (title is null) return $"no open todo #{id}.";

        using (SqliteCommand begin = connection.CreateCommand()) { begin.CommandText = "BEGIN"; begin.ExecuteNonQuery(); }
        using (SqliteCommand update = connection.CreateCommand())
        {
            update.CommandText = "UPDATE todos SET status='done' WHERE id=$i";
            update.Parameters.AddWithValue("$i", id);
            update.ExecuteNonQuery();
        }
        MutationLog.Append(connection, "todo", title, "update", "open", "done", "");
        using (SqliteCommand commit = connection.CreateCommand()) { commit.CommandText = "COMMIT"; commit.ExecuteNonQuery(); }
        return $"todo #{id} closed.";
    }

    private static string? Scalar(SqliteConnection connection, string sql, int id)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$i", id);
        object? result = command.ExecuteScalar();
        return result is null or DBNull ? null : (string)result;
    }
}
