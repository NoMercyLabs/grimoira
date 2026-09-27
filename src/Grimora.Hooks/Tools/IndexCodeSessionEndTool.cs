using System.Text.Json;
using Grimora.Brain.Schema;
using Grimora.Graph.Tools;
using Grimora.Hooks.Data;
using Grimora.Store.Schema;
using Microsoft.Data.Sqlite;

namespace Grimora.Hooks.Tools;

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
            // slice 31b: once edges.file_rel exists, keep legacy_consumes (brain_impact's read path) in
            // sync with it. The provider's own guard (2 cheap PRAGMA table_info reads, no lock needed)
            // is checked BEFORE calling SchemaRunner.Run, so the backup-and-transaction cost — and its
            // own busy-timeout wait on a locked store — is only ever paid when there is real work to do,
            // never on every session end.
            BrainLegacyConsumesFileRelSchema legacyConsumesSchema = new(connection);
            if (legacyConsumesSchema.Statements.Count > 0)
                SchemaRunner.Run(connection, [legacyConsumesSchema], backupDir);
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
