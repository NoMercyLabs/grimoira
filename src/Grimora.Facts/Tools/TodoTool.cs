using Grimora.Store.Data;
using Grimora.Store.Tools;
using Microsoft.Data.Sqlite;

namespace Grimora.Facts.Tools;

/// <summary>Records one open todo. Copied verbatim from grimora.cs's <c>AddTodo</c> (grimora.cs:639).</summary>
public sealed class TodoTool : ITool
{
    public string Name => "todo";
    public string CliVerb => "todo";
    public string? McpName => null;
    public string Help => "todo <title> [--why <reason>]         record a todo";

    public string Execute(SqliteConnection connection, string title, string why)
    {
        string ts = DateTime.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture);
        using (SqliteCommand begin = connection.CreateCommand()) { begin.CommandText = "BEGIN"; begin.ExecuteNonQuery(); }
        using (SqliteCommand insert = connection.CreateCommand())
        {
            insert.CommandText = "INSERT INTO todos(ts,title,status,why) VALUES($t,$ti,'open',$w)";
            insert.Parameters.AddWithValue("$t", ts);
            insert.Parameters.AddWithValue("$ti", title);
            insert.Parameters.AddWithValue("$w", why);
            insert.ExecuteNonQuery();
        }
        MutationLog.Append(connection, "todo", title, "insert", null, "open", why);
        using (SqliteCommand commit = connection.CreateCommand()) { commit.CommandText = "COMMIT"; commit.ExecuteNonQuery(); }
        return "todo added.";
    }
}
