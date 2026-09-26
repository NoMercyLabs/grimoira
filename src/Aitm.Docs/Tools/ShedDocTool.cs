using System.Text.Json;
using Aitm.Store.Tools;
using Microsoft.Data.Sqlite;

namespace Aitm.Docs.Tools;

/// <summary>
/// Sheds outdated absorbed docs by path substring (e.g. a superseded plan or a historical session log).
/// Copied verbatim from aitm.cs's <c>ShedDoc</c> (aitm.cs:1159) and <c>ForgetSynthesisIndex</c>
/// (aitm.cs:1108) — the read gate's own synthesis cache lives beside the store as <c>synthesis.json</c>,
/// found here from the open connection's data source rather than a passed-in instance root.
/// </summary>
public sealed class ShedDocTool : ITool
{
    public string Name => "shed-doc";
    public string CliVerb => "shed-doc";
    public string? McpName => null;
    public string Help => "shed-doc --path <s>                  drop absorbed doc sections by path substring";

    public string Execute(SqliteConnection connection, string pathFragment)
    {
        string like = "%" + pathFragment + "%";
        long before;
        using (SqliteCommand count = connection.CreateCommand())
        {
            count.CommandText = "SELECT count(*) FROM docs WHERE path LIKE $p";
            count.Parameters.AddWithValue("$p", like);
            before = (long)(count.ExecuteScalar() ?? 0L);
        }
        if (before == 0) return $"no docs match path '{pathFragment}'.";

        using (SqliteCommand begin = connection.CreateCommand()) { begin.CommandText = "BEGIN"; begin.ExecuteNonQuery(); }
        using (SqliteCommand delFts = connection.CreateCommand())
        {
            delFts.CommandText = "DELETE FROM docs_fts WHERE k IN (SELECT k FROM docs WHERE path LIKE $p)";
            delFts.Parameters.AddWithValue("$p", like);
            delFts.ExecuteNonQuery();
        }
        using (SqliteCommand delRows = connection.CreateCommand())
        {
            delRows.CommandText = "DELETE FROM docs WHERE path LIKE $p";
            delRows.Parameters.AddWithValue("$p", like);
            delRows.ExecuteNonQuery();
        }
        using (SqliteCommand commit = connection.CreateCommand()) { commit.CommandText = "COMMIT"; commit.ExecuteNonQuery(); }

        ForgetSynthesisIndex(connection, pathFragment);
        return $"shed {before} section(s) matching '{pathFragment}'.";
    }

    /// <summary>Shared with <see cref="ShedSynthesisTool"/>, which also has to drop a shed directory from
    /// the read gate's synthesis cache.</summary>
    internal static void ForgetSynthesisIndex(SqliteConnection connection, string pathFragment)
    {
        string? dataSource = connection.DataSource;
        if (string.IsNullOrEmpty(dataSource)) return;
        string? root = Path.GetDirectoryName(Path.GetFullPath(dataSource));
        if (root is null) return;
        string idx = Path.Combine(root, "synthesis.json");
        if (!File.Exists(idx)) return;
        try
        {
            string needle = pathFragment.Replace('\\', '/').ToLowerInvariant();
            List<(string key, string json)> keep = [];
            bool dropped = false;
            using (JsonDocument doc = JsonDocument.Parse(File.ReadAllText(idx)))
            {
                foreach (JsonProperty p in doc.RootElement.EnumerateObject())
                {
                    if (p.Name.Contains(needle, StringComparison.OrdinalIgnoreCase)) { dropped = true; continue; }
                    keep.Add((p.Name, p.Value.GetRawText()));
                }
            }
            if (!dropped) return;

            using FileStream fs = File.Create(idx);
            using Utf8JsonWriter w = new(fs, new JsonWriterOptions { Indented = true });
            w.WriteStartObject();
            foreach ((string k, string raw) in keep)
            {
                w.WritePropertyName(k);
                using JsonDocument v = JsonDocument.Parse(raw);
                v.RootElement.WriteTo(w);
            }
            w.WriteEndObject();
        }
        catch { /* the index is a cache; a malformed one degrades to "no synthesis known" */ }
    }
}
