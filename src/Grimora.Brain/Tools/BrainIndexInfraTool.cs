using System.Text.Json;
using static Grimora.Store.Data.JsonShape;
using Grimora.Store.Tools;
using Microsoft.Data.Sqlite;

namespace Grimora.Brain.Tools;

/// <summary>
/// The organisation is not only its repos: hosts, domains, registries and the services that run on them.
/// Copied verbatim from the standalone script <c>index-infra.mjs</c> (RESTRUCTURE.md slice 19, part 3):
/// the section-to-node-kind mapping (index-infra.mjs:35-50) is unchanged. The script used to replay the
/// fragment by spawning <c>grimora.exe spine-import</c> (index-infra.mjs:56-59); now that <c>spine-import</c>
/// is <see cref="SpineImportTool"/> in the same process, this tool calls it directly. CLI-only, no MCP
/// counterpart existed in the script.
/// </summary>
public sealed class BrainIndexInfraTool : ITool
{
    private static readonly (string Section, string Kind, string Scheme)[] Sections =
    [
        ("hosts", "platform", "host"),
        ("services", "seam", "service"),
        ("environments", "contract", "environment"),
        ("registries", "reference", "registry"),
        ("domains", "reference", "domain"),
    ];

    public string Name => "index-infra";
    public string CliVerb => "index-infra";
    public string? McpName => null;
    public string Help =>
        "index-infra --from <infra.json> [--out <fragment.json>]   reads a plain JSON description of " +
        "hosts/services/environments/registries/domains and imports it through spine-import.";

    public string ExecuteCli(SqliteConnection connection, string fromPath, string outPath)
    {
        if (!File.Exists(fromPath))
            return $"no infra description at {fromPath}\n" +
                   "expected shape: { \"hosts\": [{id,label,detail}], \"services\": [{id,label,detail,runsOn}], \"registries\": [...] }";

        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(fromPath));
        JsonElement spec = doc.RootElement;

        List<object> nodes = [];
        List<object> links = [];

        foreach ((string section, string kind, string scheme) in Sections)
        {
            if (!TryGetObjectProperty(spec, section, out JsonElement items) || items.ValueKind != JsonValueKind.Array) continue;
            foreach (JsonElement item in items.EnumerateArray())
            {
                string id = Str(item, "id");
                string key = $"{scheme}:{id}";
                nodes.Add(new
                {
                    k = key,
                    kind,
                    label = Str(item, "label") is { Length: > 0 } lbl ? lbl : id,
                    gloss = Str(item, "detail"),
                    scheme,
                    hard = Bool(item, "hard") ? 1 : 0,
                });
                string runsOn = Str(item, "runsOn");
                if (runsOn.Length > 0)
                    links.Add(new { s = key, p = "belongs_in", o = $"host:{runsOn}", because = Str(item, "why") });
                if (TryGetObjectProperty(item, "consumes", out JsonElement consumes) && consumes.ValueKind == JsonValueKind.Array)
                    foreach (JsonElement c in consumes.EnumerateArray())
                        if (c.ValueKind == JsonValueKind.String)
                            links.Add(new { s = key, p = "consumes", o = c.GetString() ?? "", because = "" });
            }
        }

        object fragment = new { nodes, slots = Array.Empty<object>(), links, aliases = Array.Empty<object>(), terms = Array.Empty<object>(), edges = Array.Empty<object>() };
        File.WriteAllText(outPath, JsonSerializer.Serialize(fragment, new JsonSerializerOptions { WriteIndented = true }));

        List<string> lines = [$"{nodes.Count} node(s), {links.Count} link(s) from {fromPath}"];
        string imported = new SpineImportTool().ExecuteCli(connection, outPath);
        lines.Add(imported.Trim().Split('\n').Last());
        return string.Join("\n", lines);
    }

    private static string Str(JsonElement e, string name) => GetString(e, name) ?? "";

    private static bool Bool(JsonElement e, string name) => IsTrue(e, name);
}
