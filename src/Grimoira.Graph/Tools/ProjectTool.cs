using Grimoira.Store.Tools;
using Microsoft.Data.Sqlite;

namespace Grimoira.Graph.Tools;

/// <summary>
/// Registers (or updates) a project root the graph indexer and edge extractor scan. Copied verbatim
/// from grimoira.cs's <c>case "project"</c> (grimoira.cs:160-166).
/// </summary>
public sealed class ProjectTool : ITool
{
    public string Name => "project";
    public string CliVerb => "project";
    public string? McpName => null;
    public string Help => "project --name <n> --root <dir>     register a project root";

    public string Execute(SqliteConnection connection, string name, string root, string lang, string globs)
    {
        // A root change is a workspace move (2026-10-05): the index-code rows written under the old root
        // point at files that are no longer there. They go now, not on the next re-index. Curated rows and
        // other projects stay (IndexCodeTool.DeleteOwnRowsUnder).
        string? oldRoot = null;
        using (SqliteCommand read = connection.CreateCommand())
        {
            read.CommandText = "SELECT root FROM projects WHERE name=$n";
            read.Parameters.AddWithValue("$n", name);
            oldRoot = read.ExecuteScalar() as string;
        }
        int dropped = 0;
        if (oldRoot is not null && !string.Equals(
                IndexCodeTool.NormalisePath(oldRoot).TrimEnd('/'), IndexCodeTool.NormalisePath(root).TrimEnd('/'),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            dropped = IndexCodeTool.DeleteOwnRowsUnder(connection, null, name, oldRoot);
        }

        using SqliteCommand upsert = connection.CreateCommand();
        upsert.CommandText = "INSERT INTO projects(name,root,lang,globs) VALUES($n,$r,$l,$g) " +
            "ON CONFLICT(name) DO UPDATE SET root=$r,lang=$l,globs=$g";
        upsert.Parameters.AddWithValue("$n", name);
        upsert.Parameters.AddWithValue("$r", root);
        upsert.Parameters.AddWithValue("$l", lang);
        upsert.Parameters.AddWithValue("$g", globs);
        upsert.ExecuteNonQuery();

        return dropped == 0
            ? $"project '{name}' registered."
            : $"project '{name}' registered. root moved: {dropped} indexed row(s) under the old root removed; run index-code --project {name}.";
    }
}
