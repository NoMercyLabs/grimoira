using System.Text.Json;

namespace Grimora.Store.Data;

/// <summary>
/// The one guarded read every JSON property lookup under src/ goes through. <see cref="JsonElement.TryGetProperty(string, out JsonElement)"/>
/// throws <see cref="InvalidOperationException"/> when the element is not an object, and outside data
/// (a transcript, an API payload, a file another tool wrote) is allowed to put a string, an array or null
/// where an object was expected: a real 3,076-line transcript had <c>toolUseResult</c> as a bare string
/// 1,167 times and as an array 1,962 times in 49,697 values, and the first classifier crashed on it.
/// This never calls <c>TryGetProperty</c> on anything but an object; the Layout test
/// <c>JsonPropertyReadsGoThroughJsonShapeTests</c> keeps it the only caller.
///
/// Grimora.Cli compiles this file as a linked copy (it references no Grimora project) and defines
/// GRIMORA_CLI_LINKED so its copy stays internal, the same way <see cref="RegexTimeout"/> is linked.
/// </summary>
#if GRIMORA_CLI_LINKED
internal static class JsonShape
#else
public static class JsonShape
#endif
{
    /// <summary>True with the property's value when <paramref name="e"/> is an object that has it; false for
    /// any other shape, including a missing property, null, a string, a number or an array.</summary>
    public static bool TryGetObjectProperty(JsonElement e, string prop, out JsonElement value)
    {
        if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out value)) return true;
        value = default;
        return false;
    }

    /// <summary>The string value of <paramref name="prop"/>, or null when <paramref name="e"/> is not an
    /// object, the property is missing, or its value is not a JSON string.</summary>
    public static string? GetString(JsonElement e, string prop) =>
        TryGetObjectProperty(e, prop, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>True only when <paramref name="prop"/> is the JSON literal <c>true</c> on an object.</summary>
    public static bool IsTrue(JsonElement e, string prop) =>
        TryGetObjectProperty(e, prop, out JsonElement v) && v.ValueKind == JsonValueKind.True;
}
