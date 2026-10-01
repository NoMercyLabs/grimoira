using System.Text;
using Grimoira.Store.Tools;
using Microsoft.Data.Sqlite;

namespace Grimoira.Facts.Tools;

/// <summary>Lists open todos. Copied verbatim from grimoira.cs's <c>ListRows</c> (grimoira.cs:592) call for the
/// <c>todos</c> verb (grimoira.cs:213).</summary>
public sealed class TodosTool : ITool
{
    public string Name => "todos";
    public string CliVerb => "todos";
    public string? McpName => null;
    public string Help => "todos                                 list open todos";

    public string Execute(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT id,title FROM todos WHERE status='open' ORDER BY id";
        using SqliteDataReader reader = command.ExecuteReader();
        StringBuilder sb = new();
        int n = 0;
        while (reader.Read()) { sb.AppendLine($"  #{reader.GetInt32(0)}  {reader.GetString(1)}"); n++; }
        sb.Append($"({n} open todo(s))");
        return sb.ToString();
    }
}
