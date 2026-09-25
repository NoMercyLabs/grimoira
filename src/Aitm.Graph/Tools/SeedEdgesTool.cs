using Aitm.Store.Tools;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Aitm.Graph.Tools;

/// <summary>
/// Loads curated cross-project edges from a spine JSON file. Copied verbatim from aitm.cs's
/// <c>SeedEdges</c> (aitm.cs:2453-2477). Not required to have a new pinned test by the slice 12 card
/// ("all except promote and seed-edges (selftest has them)").
/// </summary>
public sealed class SeedEdgesTool : ITool
{
    public string Name => "seed-edges";
    public string CliVerb => "seed-edges";
    public string? McpName => null;
    public string Help => "seed-edges [--from <spine.json>]      load curated cross-project edges from a spine file";

    public string Execute(SqliteConnection connection, string seedPath)
    {
        if (!File.Exists(seedPath)) return $"no spine file at {Path.GetFullPath(seedPath)}";

        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(seedPath));
        if (!doc.RootElement.TryGetProperty("edges", out JsonElement edges)) return "spine has no edges section.";

        static string S(JsonElement e, string n) => e.TryGetProperty(n, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        static int I(JsonElement e, string n) => e.TryGetProperty(n, out JsonElement v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;

        // file_rel (RESTRUCTURE.md slice 31b): same column-exists guard GraphFileRelSchema.HasColumn
        // already gives every reader, so a v3 store (no column yet) inserts exactly as before.
        bool hasFileRel = Schema.GraphFileRelSchema.HasColumn(connection, "edges", "file_rel");
        Dictionary<string, string> roots = hasFileRel ? Schema.GraphFileRelSchema.LoadProjectRoots(connection) : new();

        int n = 0;
        using (SqliteCommand begin = connection.CreateCommand()) { begin.CommandText = "BEGIN"; begin.ExecuteNonQuery(); }
        foreach (JsonElement e in edges.EnumerateArray())
        {
            string proj = S(e, "project");
            string file = S(e, "file");
            string? fileRel = hasFileRel && roots.TryGetValue(proj, out string? root)
                ? Schema.GraphFileRelSchema.ToRelative(root, file)
                : null;

            using SqliteCommand insert = connection.CreateCommand();
            insert.CommandText = hasFileRel
                ? "INSERT INTO edges(symbol,contract,project,file,line,usage,hardcoded,file_rel) VALUES($s,$c,$p,$f,$l,$u,$h,$fr)"
                : "INSERT INTO edges(symbol,contract,project,file,line,usage,hardcoded) VALUES($s,$c,$p,$f,$l,$u,$h)";
            insert.Parameters.AddWithValue("$s", S(e, "symbol"));
            insert.Parameters.AddWithValue("$c", S(e, "contract"));
            insert.Parameters.AddWithValue("$p", proj);
            insert.Parameters.AddWithValue("$f", file);
            insert.Parameters.AddWithValue("$l", I(e, "line"));
            insert.Parameters.AddWithValue("$u", S(e, "usage"));
            insert.Parameters.AddWithValue("$h", I(e, "hardcoded"));
            if (hasFileRel) insert.Parameters.AddWithValue("$fr", (object?)fileRel ?? DBNull.Value);
            insert.ExecuteNonQuery();
            n++;
        }
        using (SqliteCommand commit = connection.CreateCommand()) { commit.CommandText = "COMMIT"; commit.ExecuteNonQuery(); }
        return $"seeded {n} curated edge(s) from {Path.GetFullPath(seedPath)}.";
    }
}
