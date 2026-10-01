using Microsoft.Data.Sqlite;
using Grimoira.Store.Tools;

namespace Grimoira.Docs.Tools;

/// <summary>
/// Drops ONLY the synthesis for a directory, leaving the absorbed sources alone — <see cref="ShedDocTool"/>
/// matches every row under a path, which took 1352 indexed sections of a campaign with it when used to
/// remove one bad synthesis. A synthesis is a single row with a known key; removing it never needs a path
/// sweep. Copied verbatim from grimoira.cs's <c>ShedSynthesis</c> (grimoira.cs:1144), sharing
/// <see cref="ShedDocTool.ForgetSynthesisIndex"/> rather than a second copy of it.
/// </summary>
public sealed class ShedSynthesisTool : ITool
{
    public string Name => "shed-synthesis";
    public string CliVerb => "shed-synthesis";
    public string? McpName => null;
    public string Help => "shed-synthesis --path <dir>          drop the synthesis stored for a source directory";

    public string Execute(SqliteConnection connection, string sourceDir)
    {
        string dir = Path.GetFullPath(sourceDir).Replace('\\', '/').TrimEnd('/');
        string key = $"synthesis:{dir.ToLowerInvariant()}";

        long before;
        using (SqliteCommand count = connection.CreateCommand())
        {
            count.CommandText = "SELECT count(*) FROM docs WHERE k=$k";
            count.Parameters.AddWithValue("$k", key);
            before = (long)(count.ExecuteScalar() ?? 0L);
        }
        if (before == 0) return $"no synthesis stored for {dir}.";

        using (SqliteCommand begin = connection.CreateCommand()) { begin.CommandText = "BEGIN"; begin.ExecuteNonQuery(); }
        using (SqliteCommand delFts = connection.CreateCommand())
        {
            delFts.CommandText = "DELETE FROM docs_fts WHERE k=$k";
            delFts.Parameters.AddWithValue("$k", key);
            delFts.ExecuteNonQuery();
        }
        using (SqliteCommand delRow = connection.CreateCommand())
        {
            delRow.CommandText = "DELETE FROM docs WHERE k=$k";
            delRow.Parameters.AddWithValue("$k", key);
            delRow.ExecuteNonQuery();
        }
        using (SqliteCommand commit = connection.CreateCommand()) { commit.CommandText = "COMMIT"; commit.ExecuteNonQuery(); }

        ShedDocTool.ForgetSynthesisIndex(connection, dir);
        return $"shed the synthesis for {dir}.";
    }
}
