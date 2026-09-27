using System.Text.Json;

namespace Grimora.Hooks.Tests;

/// <summary>
/// Decodes a hook handler's JSON-on-stdout answer into readable form, the same decoder hook-probe.mjs
/// gave a name to (RESTRUCTURE.md slice 22, "Hooks, part 3"): a hook says nothing at all when it passes
/// a call through, so a test asserting "this is a nudge" or "this blocks" needs the same decode step
/// every time. Ported as a fixture runner inside Grimora.Hooks.Tests rather than a standalone script,
/// since every C# handler is called in process (<c>Tool.Execute(stdin)</c>), not spawned.
/// </summary>
internal static class HookProbe
{
    public static string Decode(string output)
    {
        string trimmed = output.Trim();
        if (trimmed.Length == 0) return "PASS-THROUGH — the hook said nothing, so the tool call proceeds unchanged.";

        JsonElement parsed;
        try
        {
            parsed = JsonDocument.Parse(trimmed).RootElement;
        }
        catch
        {
            return trimmed;
        }

        JsonElement h = parsed.TryGetProperty("hookSpecificOutput", out JsonElement hso) ? hso : default;

        if (parsed.TryGetProperty("decision", out JsonElement decision) && decision.ValueKind == JsonValueKind.String
            && decision.GetString() == "block")
        {
            string reason = GetString(parsed, "reason") ?? "";
            return $"BLOCK\n\n{reason}";
        }
        if (GetString(h, "permissionDecision") == "deny")
        {
            return $"DENY\n\n{GetString(h, "permissionDecisionReason") ?? ""}";
        }
        if (h.ValueKind == JsonValueKind.Object && h.TryGetProperty("updatedInput", out JsonElement updated))
        {
            string reasonLine = GetString(h, "permissionDecisionReason") is { Length: > 0 } r ? $"  {r}\n" : "";
            return $"REWRITE\n{reasonLine}\n{JsonSerializer.Serialize(updated, new JsonSerializerOptions { WriteIndented = true })}";
        }
        if (GetString(h, "additionalContext") is { Length: > 0 } ctx)
        {
            return $"CONTEXT INJECTED\n\n{ctx}";
        }
        if (GetString(parsed, "systemMessage") is { Length: > 0 } msg)
        {
            return $"MESSAGE: {msg}";
        }
        return JsonSerializer.Serialize(parsed, new JsonSerializerOptions { WriteIndented = true });
    }

    private static string? GetString(JsonElement e, string prop) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out JsonElement v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}
