using Grimora.Store.Data;
using Grimora.Store.Tools;
using Microsoft.Data.Sqlite;

namespace Grimora.Facts.Tools;

/// <summary>Resolves one open finding by id. Copied verbatim from grimora.cs's <c>ResolveFinding</c>
/// (grimora.cs:662).</summary>
public sealed class ResolveTool : ITool
{
    public string Name => "resolve";
    public string CliVerb => "resolve";
    public string? McpName => null;
    public string Help => "resolve <id>                          resolve an open finding by id";

    public string Execute(SqliteConnection connection, int id)
    {
        string? title = Scalar(connection, "SELECT title FROM findings WHERE id=$i AND status='open'", id);
        if (title is null) return $"no open finding #{id}.";

        using (SqliteCommand begin = connection.CreateCommand()) { begin.CommandText = "BEGIN"; begin.ExecuteNonQuery(); }
        using (SqliteCommand update = connection.CreateCommand())
        {
            update.CommandText = "UPDATE findings SET status='resolved' WHERE id=$i";
            update.Parameters.AddWithValue("$i", id);
            update.ExecuteNonQuery();
        }
        MutationLog.Append(connection, "finding", title, "update", "open", "resolved", "");
        using (SqliteCommand commit = connection.CreateCommand()) { commit.CommandText = "COMMIT"; commit.ExecuteNonQuery(); }
        return $"finding #{id} resolved.";
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
