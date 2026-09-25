using System.Text.Json;
using Aitm.Brain.Data;
using Aitm.Store.Tools;
using Microsoft.Data.Sqlite;

namespace Aitm.Brain.Tools;

/// <summary>
/// Replays a spine JSON file (as written by <see cref="SpineExportTool"/>) into the store: nodes and
/// slots first, then links (skipped when either endpoint is missing), then project/term aliases, then
/// raw edge rows. Copied verbatim from aitm.cs's <c>SpineImport</c> (aitm.cs:2230). CLI-only, no MCP
/// counterpart. <see cref="BrainSeedTool"/> (once moved) is only a call to this tool with the default
/// <c>seeds/spine.json</c> path.
/// </summary>
public sealed class SpineImportTool : ITool
{
    public string Name => "spine-import";
    public string CliVerb => "spine-import";
    public string? McpName => null;
    public string Help => "spine-import --from <spine.json>   replay a spine export into this store.";

    public string ExecuteCli(SqliteConnection connection, string fromPath)
    {
        if (!File.Exists(fromPath)) return $"no spine file at {fromPath}";
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(fromPath));
        JsonElement root = doc.RootElement;

        static string Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out JsonElement v) && v.ValueKind != JsonValueKind.Null
                ? (v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString())
                : "";
        static bool Flag(JsonElement e, string name) =>
            e.TryGetProperty(name, out JsonElement v) && v.ValueKind != JsonValueKind.Null &&
            (v.ValueKind == JsonValueKind.True || (v.ValueKind == JsonValueKind.Number && v.GetInt32() != 0));

        bool NodeExists(string nk) => ScalarLong(connection, "SELECT count(*) FROM node_now WHERE k=$k", ("$k", nk)) > 0;

        int n = 0, s = 0, l = 0, a = 0;
        Exec(connection, "BEGIN");
        if (root.TryGetProperty("nodes", out JsonElement nodes))
            foreach (JsonElement e in nodes.EnumerateArray())
            { BrainWriters.AddNode(connection, Str(e, "k"), Str(e, "kind"), Str(e, "label"), Str(e, "gloss"), Str(e, "scheme"), Flag(e, "hard"), "seed"); n++; }

        if (root.TryGetProperty("slots", out JsonElement slots))
            foreach (JsonElement e in slots.EnumerateArray())
            { BrainWriters.AddSlot(connection, Str(e, "frame_k"), Str(e, "name"), Str(e, "value"), Str(e, "facet"), Flag(e, "multi"), "", "seed", "seed"); s++; }

        if (root.TryGetProperty("links", out JsonElement links))
            foreach (JsonElement e in links.EnumerateArray())
            {
                string sj = Str(e, "s"), o = Str(e, "o");
                if (!NodeExists(sj) || !NodeExists(o)) { Console.Error.WriteLine($"spine: skip {sj} -> {o} (missing node)"); continue; }
                BrainWriters.AddTriple(connection, sj, Str(e, "p"), o, Str(e, "because"), "seed", false, "seed");
                l++;
            }

        if (root.TryGetProperty("aliases", out JsonElement aliases))
            foreach (JsonElement e in aliases.EnumerateArray())
            { Exec(connection, "INSERT INTO proj_alias(short,k) VALUES($a,$k) ON CONFLICT(short) DO UPDATE SET k=$k", ("$a", Str(e, "short")), ("$k", Str(e, "k"))); a++; }

        if (root.TryGetProperty("terms", out JsonElement terms))
            foreach (JsonElement e in terms.EnumerateArray())
                Exec(connection, "INSERT OR IGNORE INTO term_alias(term,canonical) VALUES($t,$c)", ("$t", Str(e, "term")), ("$c", Str(e, "canonical")));

        int g = 0;
        if (root.TryGetProperty("edges", out JsonElement edges))
            foreach (JsonElement e in edges.EnumerateArray())
            {
                int line = e.TryGetProperty("line", out JsonElement lv) && lv.ValueKind == JsonValueKind.Number ? lv.GetInt32() : 0;
                int hard = e.TryGetProperty("hardcoded", out JsonElement hv) && hv.ValueKind == JsonValueKind.Number ? hv.GetInt32() : 0;
                Exec(connection, "INSERT INTO edges(symbol,contract,project,file,line,usage,hardcoded) VALUES($s,$c,$p,$f,$l,$u,$h)",
                    ("$s", Str(e, "symbol")), ("$c", Str(e, "contract")), ("$p", Str(e, "project")),
                    ("$f", Str(e, "file")), ("$l", line), ("$u", Str(e, "usage")), ("$h", hard));
                g++;
            }

        Exec(connection, "COMMIT");
        Exec(connection, "INSERT INTO node_fts(node_fts) VALUES('rebuild')");
        return $"imported spine: {n} nodes, {s} slots, {l} links, {a} alias(es), {g} edge(s).";
    }

    private static void Exec(SqliteConnection connection, string sql, params (string name, object? value)[] parameters)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        foreach ((string name, object? value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    private static long ScalarLong(SqliteConnection connection, string sql, params (string name, object? value)[] parameters)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        foreach ((string name, object? value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return (long)(command.ExecuteScalar() ?? 0L);
    }
}
