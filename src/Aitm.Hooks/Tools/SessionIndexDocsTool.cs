using System.Text.Json;
using Aitm.Docs.Tools;
using Aitm.Hooks.Data;
using Microsoft.Data.Sqlite;

namespace Aitm.Hooks.Tools;

/// <summary>
/// SessionEnd handler, ported verbatim from session-index-docs.mjs (RESTRUCTURE.md slice 21, "Hooks,
/// part 2"): absorbs the project's AI-meta docs (<c>.claude/</c>) into the docs channel so doc recall
/// stays current as specs/plans land, without waiting for a manual <c>aitm index-docs</c>. Always fails
/// open with no output — a bad payload, a project with no <c>.claude</c> dir, an instance with no store,
/// or a locked store must never block session exit.
/// </summary>
public static class SessionIndexDocsTool
{
    public static string Execute(string stdin)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(stdin) ? "{}" : stdin);
            JsonElement payload = doc.RootElement;
            string proj = Environment.GetEnvironmentVariable("CLAUDE_PROJECT_DIR")
                ?? GetString(payload, "cwd")
                ?? Directory.GetCurrentDirectory();
            string claudeDir = Path.Combine(proj, ".claude");
            if (!Directory.Exists(claudeDir)) return "";

            string instance = HookPaths.ResolveInstance(GetString(payload, "cwd"));
            if (!File.Exists(HookPaths.DbPath(instance))) return "";

            using SqliteConnection connection = HookStore.Open(HookPaths.DbPath(instance));
            new IndexDocsTool().Execute(connection, claudeDir, "doc");
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
