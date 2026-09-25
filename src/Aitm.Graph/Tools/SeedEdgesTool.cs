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

        int n = 0;
        using (SqliteCommand begin = connection.CreateCommand()) { begin.CommandText = "BEGIN"; begin.ExecuteNonQuery(); }
        foreach (JsonElement e in edges.EnumerateArray())
        {
            using SqliteCommand insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO edges(symbol,contract,project,file,line,usage,hardcoded) VALUES($s,$c,$p,$f,$l,$u,$h)";
            insert.Parameters.AddWithValue("$s", S(e, "symbol"));
            insert.Parameters.AddWithValue("$c", S(e, "contract"));
            insert.Parameters.AddWithValue("$p", S(e, "project"));
            insert.Parameters.AddWithValue("$f", S(e, "file"));
            insert.Parameters.AddWithValue("$l", I(e, "line"));
            insert.Parameters.AddWithValue("$u", S(e, "usage"));
            insert.Parameters.AddWithValue("$h", I(e, "hardcoded"));
            insert.ExecuteNonQuery();
            n++;
        }
        using (SqliteCommand commit = connection.CreateCommand()) { commit.CommandText = "COMMIT"; commit.ExecuteNonQuery(); }
        return $"seeded {n} curated edge(s) from {Path.GetFullPath(seedPath)}.";
    }
}
