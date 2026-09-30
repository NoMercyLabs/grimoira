using System.Text.Json;
using Grimora.Brain.Data;
using Grimora.Graph.Schema;
using Grimora.Store.Tools;
using Microsoft.Data.Sqlite;
using static Grimora.Store.Data.JsonShape;

namespace Grimora.Brain.Tools;

/// <summary>
/// Replays a spine JSON file (as written by <see cref="SpineExportTool"/>) into the store: nodes and
/// slots first, then links (skipped when either endpoint is missing), then project/term aliases, then
/// raw edge rows. Copied verbatim from grimora.cs's <c>SpineImport</c> (grimora.cs:2230). CLI-only, no MCP
/// counterpart. <see cref="BrainSeedTool"/> (once moved) is only a call to this tool with the default
/// <c>seeds/spine.json</c> path.
/// </summary>
public sealed class SpineImportTool : ITool
{
    public string Name => "spine-import";
    public string CliVerb => "spine-import";
    public string? McpName => null;
    public string Help => "spine-import --from <spine.json>   replay a spine export into this store.";

    /// <param name="stderr">Where the "missing node" skip messages go. Defaults to
    /// <see cref="Console.Error"/> (RESTRUCTURE.md slice 29a) so its callers (grimora.cs, BrainSeedTool)
    /// see today's output unchanged.</param>
    public string ExecuteCli(SqliteConnection connection, string fromPath, TextWriter? stderr = null)
    {
        TextWriter target = stderr ?? Console.Error;
        if (!File.Exists(fromPath)) return $"no spine file at {fromPath}";
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(fromPath));
        JsonElement root = doc.RootElement;

        static string Str(JsonElement e, string name) =>
            TryGetObjectProperty(e, name, out JsonElement v) && v.ValueKind != JsonValueKind.Null
                ? (v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString())
                : "";
        static bool Flag(JsonElement e, string name) =>
            TryGetObjectProperty(e, name, out JsonElement v) && v.ValueKind != JsonValueKind.Null &&
            (v.ValueKind == JsonValueKind.True || (v.ValueKind == JsonValueKind.Number && v.GetInt32() != 0));

        bool NodeExists(string nk) => ScalarLong(connection, "SELECT count(*) FROM node_now WHERE k=$k", ("$k", nk)) > 0;

        int n = 0, s = 0, l = 0, a = 0;
        Exec(connection, "BEGIN");
        if (TryGetObjectProperty(root, "nodes", out JsonElement nodes) && nodes.ValueKind == JsonValueKind.Array)
            foreach (JsonElement e in nodes.EnumerateArray())
            { BrainWriters.AddNode(connection, Str(e, "k"), Str(e, "kind"), Str(e, "label"), Str(e, "gloss"), Str(e, "scheme"), Flag(e, "hard"), "seed"); n++; }

        if (TryGetObjectProperty(root, "slots", out JsonElement slots) && slots.ValueKind == JsonValueKind.Array)
            foreach (JsonElement e in slots.EnumerateArray())
            { BrainWriters.AddSlot(connection, Str(e, "frame_k"), Str(e, "name"), Str(e, "value"), Str(e, "facet"), Flag(e, "multi"), "", "seed", "seed"); s++; }

        if (TryGetObjectProperty(root, "links", out JsonElement links) && links.ValueKind == JsonValueKind.Array)
            foreach (JsonElement e in links.EnumerateArray())
            {
                string sj = Str(e, "s"), o = Str(e, "o");
                if (!NodeExists(sj) || !NodeExists(o)) { target.WriteLine($"spine: skip {sj} -> {o} (missing node)"); continue; }
                BrainWriters.AddTriple(connection, sj, Str(e, "p"), o, Str(e, "because"), "seed", false, "seed", target);
                l++;
            }

        if (TryGetObjectProperty(root, "aliases", out JsonElement aliases) && aliases.ValueKind == JsonValueKind.Array)
            foreach (JsonElement e in aliases.EnumerateArray())
            { Exec(connection, "INSERT INTO proj_alias(short,k) VALUES($a,$k) ON CONFLICT(short) DO UPDATE SET k=$k", ("$a", Str(e, "short")), ("$k", Str(e, "k"))); a++; }

        if (TryGetObjectProperty(root, "terms", out JsonElement terms) && terms.ValueKind == JsonValueKind.Array)
            foreach (JsonElement e in terms.EnumerateArray())
                Exec(connection, "INSERT OR IGNORE INTO term_alias(term,canonical) VALUES($t,$c)", ("$t", Str(e, "term")), ("$c", Str(e, "canonical")));

        // file_rel (RESTRUCTURE.md slice 31b): same column-exists guard GraphFileRelSchema.HasColumn
        // already gives every reader, so a v3 store (no column yet) inserts exactly as before.
        bool hasFileRel = GraphFileRelSchema.HasColumn(connection, "edges", "file_rel");
        Dictionary<string, string> roots = hasFileRel ? GraphFileRelSchema.LoadProjectRoots(connection) : [];

        int g = 0;
        if (TryGetObjectProperty(root, "edges", out JsonElement edges) && edges.ValueKind == JsonValueKind.Array)
            foreach (JsonElement e in edges.EnumerateArray())
            {
                int line = TryGetObjectProperty(e, "line", out JsonElement lv) && lv.ValueKind == JsonValueKind.Number ? lv.GetInt32() : 0;
                int hard = TryGetObjectProperty(e, "hardcoded", out JsonElement hv) && hv.ValueKind == JsonValueKind.Number ? hv.GetInt32() : 0;
                string proj = Str(e, "project");
                string file = Str(e, "file");
                string? fileRel = hasFileRel && roots.TryGetValue(proj, out string? projRoot)
                    ? GraphFileRelSchema.ToRelative(projRoot, file)
                    : null;
                if (hasFileRel)
                    Exec(connection, "INSERT INTO edges(symbol,contract,project,file,line,usage,hardcoded,file_rel) VALUES($s,$c,$p,$f,$l,$u,$h,$fr)",
                        ("$s", Str(e, "symbol")), ("$c", Str(e, "contract")), ("$p", proj),
                        ("$f", file), ("$l", line), ("$u", Str(e, "usage")), ("$h", hard), ("$fr", fileRel));
                else
                    Exec(connection, "INSERT INTO edges(symbol,contract,project,file,line,usage,hardcoded) VALUES($s,$c,$p,$f,$l,$u,$h)",
                        ("$s", Str(e, "symbol")), ("$c", Str(e, "contract")), ("$p", proj),
                        ("$f", file), ("$l", line), ("$u", Str(e, "usage")), ("$h", hard));
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
