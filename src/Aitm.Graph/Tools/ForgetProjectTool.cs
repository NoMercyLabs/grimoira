using Aitm.Store.Tools;
using Microsoft.Data.Sqlite;

namespace Aitm.Graph.Tools;

/// <summary>
/// Unregisters a project and drops its edges. Copied verbatim from aitm.cs's <c>case "forget-project"</c>
/// (aitm.cs:172-180). Registration without deregistration left roots that no longer exist on disk still
/// answering questions with paths that are gone, which is worse than not knowing.
///
/// RESTRUCTURE.md section 5 ("Protecting the system from the agents it serves") says the CLI admin verbs
/// that delete or bulk-change, including <c>forget-project</c>, make an automatic backup first. Slice 11
/// (docs/RESTRUCTURE.md) does not name that step, and its exit is "the standard check" only, so no
/// backup is added here — flagged for whichever slice wires the backup-before-delete rule in.
/// </summary>
public sealed class ForgetProjectTool : ITool
{
    public string Name => "forget-project";
    public string CliVerb => "forget-project";
    public string? McpName => null;
    public string Help => "forget-project --name <n>           unregister a project and drop its edges";

    public string Execute(SqliteConnection connection, string name)
    {
        long dropped;
        using (SqliteCommand count = connection.CreateCommand())
        {
            count.CommandText = "SELECT count(*) FROM edges WHERE project=$n";
            count.Parameters.AddWithValue("$n", name);
            dropped = (long)(count.ExecuteScalar() ?? 0L);
        }
        using (SqliteCommand delEdges = connection.CreateCommand())
        {
            delEdges.CommandText = "DELETE FROM edges WHERE project=$n";
            delEdges.Parameters.AddWithValue("$n", name);
            delEdges.ExecuteNonQuery();
        }
        using (SqliteCommand delProject = connection.CreateCommand())
        {
            delProject.CommandText = "DELETE FROM projects WHERE name=$n";
            delProject.Parameters.AddWithValue("$n", name);
            delProject.ExecuteNonQuery();
        }

        return $"project '{name}' forgotten ({dropped} edge(s) dropped).";
    }
}
