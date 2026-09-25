using System.Text.Json;
using Aitm.Graph.Tools;
using Aitm.Hooks.Data;
using Microsoft.Data.Sqlite;

namespace Aitm.Hooks.Tools;

/// <summary>
/// SessionEnd handler for the <c>index-code</c> slot, ported verbatim from the standalone
/// <c>index-code.mjs --quiet</c> invocation hooks.json's SessionEnd list runs today (RESTRUCTURE.md
/// slice 21, "Hooks, part 2"): bulk-indexes every registered project's public declaration surface into
/// <c>edges</c>, idempotently. Always fails open with no output — a bad payload, an instance with no
/// store, or a locked store must never block session exit.
/// </summary>
public static class IndexCodeSessionEndTool
{
    public static string Execute(string stdin)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(stdin) ? "{}" : stdin);
            JsonElement payload = doc.RootElement;
            string instance = HookPaths.ResolveInstance(GetString(payload, "cwd"));
            if (!File.Exists(HookPaths.DbPath(instance))) return "";

            using SqliteConnection connection = HookStore.Open(HookPaths.DbPath(instance));
            string backupDir = Path.Combine(HookPaths.InstanceDir(instance), "backups");
            new IndexCodeTool().Execute(connection, null, backupDir);
        }
        catch
        {
            // fail open — never block session end on an index error
        }
        return "";
    }

    private static string? GetString(JsonElement e, string prop) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out JsonElement v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}
