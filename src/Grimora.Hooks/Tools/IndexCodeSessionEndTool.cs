using System.Text.Json;
using static Grimora.Store.Data.JsonShape;
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
        TryExecute(stdin);
        return "";
    }

    /// <summary>Same work as <see cref="Execute"/>, but the exception is returned instead of swallowed, so a
    /// caller that runs this off the request thread (the SessionEnd index queue) can record a real failure
    /// instead of it vanishing silently. Null means the code was indexed, or there was nothing to do (an
    /// instance with no store yet) - both are a normal outcome, not a failure worth recording.</summary>
    public static Exception? TryExecute(string stdin)
    {
        try
        {
            ExecuteCore(stdin);
            return null;
        }
        catch (Exception e)
        {
            return e;
        }
    }

    private static void ExecuteCore(string stdin)
    {
        using JsonDocument doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(stdin) ? "{}" : stdin);
        JsonElement payload = doc.RootElement;
        string instance = HookPaths.ResolveInstance(GetString(payload, "cwd"));
        if (!File.Exists(HookPaths.DbPath(instance))) return;

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
}
