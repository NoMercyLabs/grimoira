using System.Text.Json;
using Grimora.Docs.Tools;
using Grimora.Hooks.Data;
using Microsoft.Data.Sqlite;

namespace Grimora.Hooks.Tools;

/// <summary>
/// SessionEnd handler, ported verbatim from session-index-docs.mjs (RESTRUCTURE.md slice 21, "Hooks,
/// part 2"): absorbs the project's AI-meta docs (<c>.claude/</c>) into the docs channel so doc recall
/// stays current as specs/plans land, without waiting for a manual <c>grimora index-docs</c>. Always fails
/// open with no output — a bad payload, a project with no <c>.claude</c> dir, an instance with no store,
/// or a locked store must never block session exit.
/// </summary>
public static class SessionIndexDocsTool
{
    public static string Execute(string stdin)
    {
        TryExecute(stdin);
        return "";
    }

    /// <summary>Same work as <see cref="Execute"/>, but the exception is returned instead of swallowed, so a
    /// caller that runs this off the request thread (the SessionEnd index queue) can record a real failure
    /// instead of it vanishing silently. Null means the docs were indexed, or there was nothing to do
    /// (no `.claude` dir, no store yet) - both are a normal outcome, not a failure worth recording.</summary>
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
        // Reviewer finding: this used to prefer the server's own CLAUDE_PROJECT_DIR env over the request's
        // cwd. That was already wrong per-request (the shared server's env is whichever session started
        // it, not this call's); now that this runs off the SessionEnd queue instead of inline with the
        // request, there is no meaningful "this call's env" at all - the payload's own cwd is the only
        // correct source, same order RequestProjectResolver already uses for every other hook/tool call.
        string proj = GetString(payload, "cwd")
            ?? Environment.GetEnvironmentVariable("CLAUDE_PROJECT_DIR")
            ?? Directory.GetCurrentDirectory();
        string claudeDir = Path.Combine(proj, ".claude");
        if (!Directory.Exists(claudeDir)) return;

        string instance = HookPaths.ResolveInstance(GetString(payload, "cwd"));
        if (!File.Exists(HookPaths.DbPath(instance))) return;

        using SqliteConnection connection = HookStore.Open(HookPaths.DbPath(instance));
        new IndexDocsTool().Execute(connection, claudeDir, "doc");
    }

    private static string? GetString(JsonElement e, string prop) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out JsonElement v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}
