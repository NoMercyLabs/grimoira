using System.Text.Json;
using System.Text.RegularExpressions;
using Aitm.Store.Tools;

namespace Aitm.Hooks.Tools;

/// <summary>
/// Checks Claude hook registrations without running hooks or printing their commands, ported from
/// hook-doctor.mjs (RESTRUCTURE.md slice 22, "Hooks, part 3"): an enabled plugin plus matching direct
/// hooks would run the same AITM script twice. <see cref="Audit"/> is <c>auditHooks</c>, ported
/// verbatim (hook-doctor.test.mjs's 3 cases). <see cref="ExecuteCli"/> is the file-reading entry point,
/// taking every settings/plugin path as a parameter so a caller — including a test — never has to point
/// it at the real <c>~/.claude</c> settings.
/// </summary>
public sealed class HookDoctorTool : ITool
{
    private static readonly Regex ScriptNameRe = new(@"[a-z0-9-]+\.mjs\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex AitmPathRe = new(@"(^|[/\\])aitm[/\\]", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public string Name => "hooks-doctor";
    public string CliVerb => "hooks-doctor";
    public string? McpName => null;
    public string Help => "hooks-doctor [--project <dir>]        find duplicate hook registrations (plugin vs. direct)";

    /// <summary>Ported verbatim from hook-doctor.mjs's <c>auditHooks(user, project, plugin)</c>: each
    /// argument is a parsed <c>settings.json</c>/<c>hooks.json</c> document (or an empty object).</summary>
    public static HookAuditResult Audit(JsonElement user, JsonElement project, JsonElement plugin)
    {
        Dictionary<string, bool> enabled = new(StringComparer.Ordinal);
        foreach (JsonElement doc in new[] { user, project })
            foreach ((string name, bool value) in EnabledPlugins(doc))
                enabled[name] = value;
        bool pluginEnabledSetting = enabled.Any(kv => Regex.IsMatch(kv.Key, "^aitm@", RegexOptions.IgnoreCase) && kv.Value);

        List<string> direct = [.. Hooks(user, directOnly: true), .. Hooks(project, directOnly: true)];
        HashSet<string> pluginSet = new(Hooks(plugin, directOnly: false), StringComparer.Ordinal);

        Dictionary<string, int> directCounts = new(StringComparer.Ordinal);
        foreach (string name in direct) directCounts[name] = directCounts.GetValueOrDefault(name) + 1;
        HashSet<string> directSet = new(direct, StringComparer.Ordinal);

        List<string> overlap = [.. pluginSet.Where(directSet.Contains).OrderBy(n => n, StringComparer.Ordinal)];
        List<string> directOnly = [.. directSet.Where(n => !pluginSet.Contains(n)).OrderBy(n => n, StringComparer.Ordinal)];
        List<string> directDuplicates = [.. directCounts.Where(kv => kv.Value > 1).Select(kv => kv.Key).OrderBy(n => n, StringComparer.Ordinal)];

        bool isUnsafe = directDuplicates.Count > 0 || (pluginEnabledSetting && overlap.Count > 0);
        return new HookAuditResult(pluginEnabledSetting, pluginSet.Count, directSet.Count, overlap, directOnly, directDuplicates, isUnsafe);
    }

    /// <summary>Reads the 3 settings/plugin files, runs <see cref="Audit"/>, and returns the same plain
    /// text line the .mjs prints. A missing file reads as an empty document, same as the .mjs's
    /// <c>readJson</c> (ENOENT -&gt; <c>{}</c>).</summary>
    public static string ExecuteCli(string userSettingsPath, string projectSettingsPath, string pluginHooksPath, out int exitCode)
    {
        JsonElement user = ReadJson(userSettingsPath);
        JsonElement project = ReadJson(projectSettingsPath);
        JsonElement plugin = ReadJson(pluginHooksPath);
        HookAuditResult result = Audit(user, project, plugin);
        exitCode = result.Unsafe ? 2 : 0;
        return $"AITM hooks: {result.PluginHooks} in plugin, {result.DirectHooks} direct, {result.Overlap.Count} overlapping, " +
            $"{result.DirectOnly.Count} direct only. Plugin enabled in settings: {result.PluginEnabledSetting}. " +
            $"{(result.Unsafe ? "FAIL: duplicate hook launches possible." : "No duplicate indicated by settings.")}";
    }

    private static JsonElement ReadJson(string path)
    {
        try
        {
            return JsonDocument.Parse(File.ReadAllText(path)).RootElement;
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return JsonDocument.Parse("{}").RootElement;
        }
    }

    private static IEnumerable<(string Name, bool Value)> EnabledPlugins(JsonElement doc)
    {
        if (doc.ValueKind != JsonValueKind.Object || !doc.TryGetProperty("enabledPlugins", out JsonElement ep)
            || ep.ValueKind != JsonValueKind.Object) yield break;
        foreach (JsonProperty p in ep.EnumerateObject())
            yield return (p.Name, p.Value.ValueKind == JsonValueKind.True);
    }

    private static IEnumerable<string> Hooks(JsonElement data, bool directOnly)
    {
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("hooks", out JsonElement hooksEl)
            || hooksEl.ValueKind != JsonValueKind.Object) yield break;

        foreach (JsonProperty eventProp in hooksEl.EnumerateObject())
        {
            string @event = eventProp.Name;
            if (eventProp.Value.ValueKind != JsonValueKind.Array) continue;
            foreach (JsonElement group in eventProp.Value.EnumerateArray())
            {
                if (group.ValueKind != JsonValueKind.Object || !group.TryGetProperty("hooks", out JsonElement hookList)
                    || hookList.ValueKind != JsonValueKind.Array) continue;
                foreach (JsonElement hook in hookList.EnumerateArray())
                {
                    string command = GetString(hook, "command") ?? "";
                    if (directOnly && !AitmPathRe.IsMatch(command)) continue;

                    List<string> parts = [command];
                    if (hook.ValueKind == JsonValueKind.Object && hook.TryGetProperty("args", out JsonElement args)
                        && args.ValueKind == JsonValueKind.Array)
                        parts.AddRange(args.EnumerateArray().Select(a => a.ValueKind == JsonValueKind.String ? a.GetString() ?? "" : ""));

                    string joined = string.Join(" ", parts);
                    foreach (Match m in ScriptNameRe.Matches(joined))
                        yield return $"{@event}:{m.Value.ToLowerInvariant()}";
                }
            }
        }
    }

    private static string? GetString(JsonElement e, string prop) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out JsonElement v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}
