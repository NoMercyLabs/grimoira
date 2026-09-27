using System.Globalization;
using System.Text.Json;
using Grimora.Store.Tools;
using Microsoft.Data.Sqlite;

namespace Grimora.Brain.Tools;

/// <summary>
/// The curated spine is per-ecosystem knowledge, not engine code, so it lives in a JSON file next to
/// the store rather than compiled into the binary. Export reads whatever is currently seeded so it can
/// be versioned, shared, or swapped out entirely without touching this program. Copied verbatim from
/// grimora.cs's <c>SpineExport</c> (grimora.cs:2174). CLI-only, no MCP counterpart.
/// </summary>
public sealed class SpineExportTool : ITool
{
    public string Name => "spine-export";
    public string CliVerb => "spine-export";
    public string? McpName => null;
    public string Help =>
        "spine-export [--to <path>]   export the curated spine (nodes, slots, seed links, project " +
        "aliases, term aliases, edges) to a JSON file, default seeds/spine.json.";

    public string ExecuteCli(SqliteConnection connection, string toPath)
    {
        List<Dictionary<string, object?>> Rows(string sql)
        {
            List<Dictionary<string, object?>> rows = [];
            using SqliteCommand c = connection.CreateCommand();
            c.CommandText = sql;
            using SqliteDataReader r = c.ExecuteReader();
            while (r.Read())
            {
                Dictionary<string, object?> row = [];
                for (int i = 0; i < r.FieldCount; i++) row[r.GetName(i)] = r.IsDBNull(i) ? null : r.GetValue(i);
                rows.Add(row);
            }
            return rows;
        }

        (string name, List<Dictionary<string, object?>> rows)[] sections =
        [
            ("nodes", Rows("SELECT k,kind,label,gloss,COALESCE(scheme,'') AS scheme,hard FROM node WHERE valid_to IS NULL ORDER BY k")),
            ("slots", Rows("SELECT frame_k,name,value,COALESCE(facet,'text') AS facet,multi FROM slot WHERE valid_to IS NULL ORDER BY frame_k,name")),
            ("links", Rows("SELECT s,p,o,COALESCE(because,'') AS because FROM triple WHERE valid_to IS NULL AND o_is_literal=0 AND src='seed' ORDER BY s,p,o")),
            ("aliases", Rows("SELECT short,k FROM proj_alias ORDER BY short")),
            ("terms", Rows("SELECT term,canonical FROM term_alias ORDER BY term,canonical")),
            ("edges", Rows("SELECT symbol,COALESCE(contract,'') AS contract,COALESCE(project,'') AS project,file,line,COALESCE(usage,'') AS usage,COALESCE(hardcoded,0) AS hardcoded FROM edges WHERE contract <> 'decl' ORDER BY symbol,file")),
        ];

        // Written field by field rather than via reflection-based serialization: the shape is small and fixed.
        string full = Path.GetFullPath(toPath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        using (FileStream fs = File.Create(full))
        using (Utf8JsonWriter w = new(fs, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            foreach ((string name, List<Dictionary<string, object?>> rows) in sections)
            {
                w.WriteStartArray(name);
                foreach (Dictionary<string, object?> row in rows)
                {
                    w.WriteStartObject();
                    foreach (KeyValuePair<string, object?> cell in row)
                    {
                        if (cell.Value is long l) w.WriteNumber(cell.Key, l);
                        else if (cell.Value is null) w.WriteNull(cell.Key);
                        else w.WriteString(cell.Key, Convert.ToString(cell.Value, CultureInfo.InvariantCulture) ?? "");
                    }
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }
            w.WriteEndObject();
        }
        return $"exported spine to {full}: " + string.Join(", ", sections.Select(x => $"{x.rows.Count} {x.name}")) + ".";
    }
}
