using Grimoira.Store.Tools;
using Microsoft.Data.Sqlite;

namespace Grimoira.Graph.Tools;

/// <summary>
/// Lists every registered project root. Copied verbatim from grimoira.cs's <c>ListProjects</c>
/// (grimoira.cs:2817-2825).
/// </summary>
public sealed class ProjectsTool : ITool
{
    public string Name => "projects";
    public string CliVerb => "projects";
    public string? McpName => null;
    public string Help => "projects                            list registered projects";

    public string Execute(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT name,root,lang,globs FROM projects ORDER BY name";
        using SqliteDataReader reader = command.ExecuteReader();
        List<string> lines = [];
        int n = 0;
        while (reader.Read())
        {
            lines.Add($"  {reader.GetString(0),-10} [{reader.GetString(2)}]  {reader.GetString(1)}  ({reader.GetString(3)})");
            n++;
        }
        lines.Add($"({n} project(s) registered)");
        return string.Join(Environment.NewLine, lines);
    }
}
