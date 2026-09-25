using System.Text.Json;
using Aitm.Hooks.Data;
using Aitm.Memory.Tools;
using Microsoft.Data.Sqlite;

namespace Aitm.Hooks.Tools;

/// <summary>
/// SessionEnd handler, ported verbatim from session-index.mjs (RESTRUCTURE.md slice 21, "Hooks, part
/// 2"): folds the session transcript into the chat-recall channel automatically, so recall stays current
/// without waiting for a manual <c>aitm index-chat</c>. Indexing is idempotent (<c>chat.k</c> is a primary
/// key). Always fails open with no output — a bad payload, a missing transcript, an instance with no
/// store, or a locked store must never block session exit. Aitm.Hooks sits at reference level 3
/// (ReferenceDirectionTests), so unlike the .mjs (which shells out to the prebuilt CLI) this calls
/// <see cref="IndexChatTool"/> in process.
/// </summary>
public static class SessionIndexChatTool
{
    public static string Execute(string stdin)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(stdin) ? "{}" : stdin);
            JsonElement payload = doc.RootElement;
            string? transcriptPath = GetString(payload, "transcript_path");
            if (transcriptPath is null || !File.Exists(transcriptPath)) return "";

            string instance = HookPaths.ResolveInstance(GetString(payload, "cwd"));
            if (!File.Exists(HookPaths.DbPath(instance))) return "";

            using SqliteConnection connection = HookStore.Open(HookPaths.DbPath(instance));
            new IndexChatTool().Execute(connection, transcriptPath);
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
