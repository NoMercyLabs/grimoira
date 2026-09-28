using Microsoft.Data.Sqlite;

namespace Grimora.Server.Handover;

/// <summary>Finds the workspace scripts from a request root or registered project roots.</summary>
public static class WorkspaceRootResolver
{
    public static string Resolve(string candidate, SqliteConnection connection)
    {
        if (WithScripts(candidate) is { } root) return root;
        using SqliteCommand projects = connection.CreateCommand();
        projects.CommandText = "SELECT root FROM projects ORDER BY root";
        using SqliteDataReader reader = projects.ExecuteReader();
        while (reader.Read())
        {
            if (!reader.IsDBNull(0) && WithScripts(reader.GetString(0)) is { } registered) return registered;
        }
        return candidate;
    }

    private static string? WithScripts(string start)
    {
        if (string.IsNullOrWhiteSpace(start) || !Directory.Exists(start)) return null;
        DirectoryInfo? dir = new(Path.GetFullPath(start));
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, ".claude", "scripts", "workspace-capabilities.py")) ||
                File.Exists(Path.Combine(dir.FullName, "scripts", "workspace-capabilities.py"))) return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }
}
