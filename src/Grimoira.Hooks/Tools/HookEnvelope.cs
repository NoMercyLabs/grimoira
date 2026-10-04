using System.Text.Json;
using static Grimoira.Store.Data.JsonShape;

namespace Grimoira.Hooks.Tools;

/// <summary>
/// Joins two hook answers into one. Claude Code reads one JSON envelope per hook command; the CLI's
/// UserPromptSubmit has two sources (the local compaction restore, then the server's prompt recall), and
/// printing both would be broken JSON. One empty side gives the other; two envelopes with
/// <c>additionalContext</c> give one envelope, the first text first. Any shape that cannot be merged
/// gives the first answer alone (fail open: the restore is never lost to a bad recall).
/// Linked into Grimoira.Cli as source, the same way <see cref="CompactRestoreTool"/> is.
/// </summary>
public static class HookEnvelope
{
    public static string Merge(string a, string b)
    {
        if (string.IsNullOrWhiteSpace(a)) return b;
        if (string.IsNullOrWhiteSpace(b)) return a;
        try
        {
            using JsonDocument first = JsonDocument.Parse(a);
            using JsonDocument second = JsonDocument.Parse(b);
            string? eventName = Read(first, "hookEventName");
            string? firstContext = Read(first, "additionalContext");
            string? secondContext = Read(second, "additionalContext");
            if (eventName is null || firstContext is null || secondContext is null) return a;

            var envelope = new
            {
                hookSpecificOutput = new
                {
                    hookEventName = eventName,
                    additionalContext = $"{firstContext}\n\n{secondContext}",
                },
            };
            return JsonSerializer.Serialize(envelope);
        }
        catch
        {
            // fail open
            return a;
        }
    }

    private static string? Read(JsonDocument doc, string property) =>
        TryGetObjectProperty(doc.RootElement, "hookSpecificOutput", out JsonElement hso)
            ? GetString(hso, property)
            : null;
}
