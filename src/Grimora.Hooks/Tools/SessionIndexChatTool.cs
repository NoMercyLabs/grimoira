using System.Text.Json;
using static Grimora.Store.Data.JsonShape;
using Grimora.Hooks.Data;
using Grimora.Memory.Tools;
using Microsoft.Data.Sqlite;

namespace Grimora.Hooks.Tools;

/// <summary>
/// SessionEnd handler, ported verbatim from session-index.mjs (RESTRUCTURE.md slice 21, "Hooks, part
/// 2"): folds the session transcript into the chat-recall channel automatically, so recall stays current
/// without waiting for a manual <c>grimora index-chat</c>. Indexing is idempotent (<c>chat.k</c> is a primary
/// key). Always fails open with no output — a bad payload, a missing transcript, an instance with no
/// store, or a locked store must never block session exit. Grimora.Hooks sits at reference level 3
/// (ReferenceDirectionTests), so unlike the .mjs (which shells out to the prebuilt CLI) this calls
/// <see cref="IndexChatTool"/> in process.
/// </summary>
public static class SessionIndexChatTool
{
    public static string Execute(string stdin)
    {
        TryExecute(stdin);
        return "";
    }

    /// <summary>Same work as <see cref="Execute"/>, but the exception is returned instead of swallowed, so a
    /// caller that runs this off the request thread (the SessionEnd index queue) can record a real failure
    /// instead of it vanishing silently. Null means the transcript was indexed, or there was nothing to do
    /// (no transcript, no store yet) - both are a normal outcome, not a failure worth recording.</summary>
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
        string? transcriptPath = GetString(payload, "transcript_path");
        if (transcriptPath is null || !File.Exists(transcriptPath)) return;

        string instance = HookPaths.ResolveInstance(GetString(payload, "cwd"));
        if (!File.Exists(HookPaths.DbPath(instance))) return;

        using SqliteConnection connection = HookStore.Open(HookPaths.DbPath(instance));
        new IndexChatTool().Execute(connection, transcriptPath);
    }
}
