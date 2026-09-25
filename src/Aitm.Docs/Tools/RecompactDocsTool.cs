using Aitm.Store.Tools;
using Microsoft.Data.Sqlite;

namespace Aitm.Docs.Tools;

/// <summary>
/// Re-densifies already-stored docs (a one-off after a storage-format change). Reports the char delta;
/// run tok.cs before/after for the real token delta. Copied verbatim from aitm.cs's <c>RecompactDocs</c>
/// (aitm.cs:955), reusing the same <c>Compact</c> as <see cref="IndexDocsTool"/> (aitm.cs:913).
/// </summary>
public sealed class RecompactDocsTool : ITool
{
    public string Name => "recompact-docs";
    public string CliVerb => "recompact-docs";
    public string? McpName => null;
    public string Help => "recompact-docs                       re-densify already-stored docs after a storage-format change";

    public string Execute(SqliteConnection connection)
    {
        List<(string k, string title, string content)> rows = new();
        using (SqliteCommand select = connection.CreateCommand())
        {
            select.CommandText = "SELECT k, title, content FROM docs";
            using SqliteDataReader reader = select.ExecuteReader();
            while (reader.Read()) rows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        }

        long before = rows.Sum(x => (long)x.content.Length);
        long after = 0;
        int changed = 0;

        using (SqliteCommand begin = connection.CreateCommand()) { begin.CommandText = "BEGIN"; begin.ExecuteNonQuery(); }
        foreach ((string k, string title, string content) in rows)
        {
            string compact = IndexDocsTool.Compact(content);
            after += compact.Length;
            if (compact != content)
            {
                using (SqliteCommand update = connection.CreateCommand())
                {
                    update.CommandText = "UPDATE docs SET content=$co WHERE k=$k";
                    update.Parameters.AddWithValue("$co", compact);
                    update.Parameters.AddWithValue("$k", k);
                    update.ExecuteNonQuery();
                }
                using (SqliteCommand del = connection.CreateCommand())
                {
                    del.CommandText = "DELETE FROM docs_fts WHERE k=$k";
                    del.Parameters.AddWithValue("$k", k);
                    del.ExecuteNonQuery();
                }
                using (SqliteCommand ins = connection.CreateCommand())
                {
                    ins.CommandText = "INSERT INTO docs_fts(k,title,content) VALUES($k,$t,$co)";
                    ins.Parameters.AddWithValue("$k", k);
                    ins.Parameters.AddWithValue("$t", title);
                    ins.Parameters.AddWithValue("$co", compact);
                    ins.ExecuteNonQuery();
                }
                changed++;
            }
        }
        using (SqliteCommand commit = connection.CreateCommand()) { commit.CommandText = "COMMIT"; commit.ExecuteNonQuery(); }

        double pct = before == 0 ? 0 : 100.0 * (before - after) / before;
        return $"recompacted {changed}/{rows.Count} section(s): {before} -> {after} chars ({pct:F0}% smaller). Run tok.cs for the token delta.";
    }
}
