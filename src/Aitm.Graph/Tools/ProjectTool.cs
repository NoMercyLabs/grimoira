using Aitm.Store.Tools;
using Microsoft.Data.Sqlite;

namespace Aitm.Graph.Tools;

/// <summary>
/// Registers (or updates) a project root the graph indexer and edge extractor scan. Copied verbatim
/// from aitm.cs's <c>case "project"</c> (aitm.cs:160-166).
/// </summary>
public sealed class ProjectTool : ITool
{
    public string Name => "project";
    public string CliVerb => "project";
    public string? McpName => null;
    public string Help => "project --name <n> --root <dir>     register a project root";

    public string Execute(SqliteConnection connection, string name, string root, string lang, string globs)
    {
        using SqliteCommand upsert = connection.CreateCommand();
        upsert.CommandText = "INSERT INTO projects(name,root,lang,globs) VALUES($n,$r,$l,$g) " +
            "ON CONFLICT(name) DO UPDATE SET root=$r,lang=$l,globs=$g";
        upsert.Parameters.AddWithValue("$n", name);
        upsert.Parameters.AddWithValue("$r", root);
        upsert.Parameters.AddWithValue("$l", lang);
        upsert.Parameters.AddWithValue("$g", globs);
        upsert.ExecuteNonQuery();

        return $"project '{name}' registered.";
    }
}
