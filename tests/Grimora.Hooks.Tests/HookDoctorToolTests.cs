using System.Text.Json;
using Grimora.Hooks.Tools;
using Xunit;

namespace Grimora.Hooks.Tests;

/// <summary>
/// RESTRUCTURE.md slice 22 ("Hooks, part 3"): hook-doctor.mjs moves in as <see cref="HookDoctorTool"/>.
/// <see cref="HookDoctorTool.Audit"/> ports <c>auditHooks</c> from hook-doctor.test.mjs's 3 cases
/// verbatim. <see cref="HookDoctorTool.ExecuteCli"/> is pinned separately against fixture settings and
/// plugin files (never the real <c>~/.claude</c> settings — every path here is a temp file this test
/// writes and injects), and must give the same counts and message shape the .mjs prints.
/// </summary>
public class HookDoctorToolTests
{
    private static JsonElement Direct(string name) =>
        JsonDocument.Parse($$"""{"command":"node C:/Projects/grimora/{{name}}.mjs"}""").RootElement;

    private static JsonElement Plugin(string name) =>
        JsonDocument.Parse($$"""{"command":"node","args":["${CLAUDE_PLUGIN_ROOT}/{{name}}.mjs"]}""").RootElement;

    private static JsonElement Settings(string json) => JsonDocument.Parse(json).RootElement;

    private static string HooksDoc(string @event, JsonElement hook) =>
        "{\"hooks\":{\"" + @event + "\":[{\"hooks\":[" + hook.GetRawText() + "]}]}}";

    [Fact]
    public void ADormantPluginOverlapDoesNotClaimDuplicateExecution()
    {
        HookAuditResult result = HookDoctorTool.Audit(
            Settings(HooksDoc("Stop", Direct("continue-guard"))),
            Settings("{}"),
            Settings(HooksDoc("Stop", Plugin("continue-guard"))));

        Assert.False(result.Unsafe);
        Assert.Equal(new[] { "Stop:continue-guard.mjs" }, result.Overlap);
    }

    [Fact]
    public void EnablingAnOverlappingPluginReportsDuplicateExecution()
    {
        HookAuditResult result = HookDoctorTool.Audit(
            Settings(HooksDoc("Stop", Direct("continue-guard"))),
            Settings("""{"enabledPlugins":{"grimora@nomercylabs":true}}"""),
            Settings(HooksDoc("Stop", Plugin("continue-guard"))));

        Assert.True(result.Unsafe);
    }

    [Fact]
    public void DirectHooksRepeatedAcrossScopesAreUnsafeIndependentlyOfThePlugin()
    {
        HookAuditResult result = HookDoctorTool.Audit(
            Settings(HooksDoc("Stop", Direct("continue-guard"))),
            Settings(HooksDoc("Stop", Direct("continue-guard"))),
            Settings("{}"));

        Assert.Equal(new[] { "Stop:continue-guard.mjs" }, result.DirectDuplicates);
        Assert.True(result.Unsafe);
    }

    // --- ExecuteCli: fixture files, never the real ~/.claude settings ---

    private static string NewTempDir(string label)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"test-hooks-doctor-{label}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void FixtureFilesGiveTheSameCountsAndMessageAsTheMjs()
    {
        string root = NewTempDir("fixtures");
        string userSettings = Path.Combine(root, "user-settings.json");
        string projectSettings = Path.Combine(root, "project-settings.json");
        string pluginHooks = Path.Combine(root, "plugin-hooks.json");

        File.WriteAllText(userSettings, HooksDoc("Stop", Direct("continue-guard")));
        File.WriteAllText(projectSettings, "{}");
        File.WriteAllText(pluginHooks, HooksDoc("Stop", Plugin("continue-guard")));

        string message = HookDoctorTool.ExecuteCli(userSettings, projectSettings, pluginHooks, out int exitCode);

        // Same shape as the .mjs's plain-text line: "Grimora hooks: N in plugin, N direct, N overlapping,
        // N direct only. Plugin enabled in settings: bool. <verdict>."
        Assert.Equal(
            "Grimora hooks: 1 in plugin, 1 direct, 1 overlapping, 0 direct only. Plugin enabled in settings: False. "
            + "No duplicate indicated by settings.",
            message);
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public void MissingFixtureFilesReadAsEmptySettings()
    {
        string root = NewTempDir("missing");
        string userSettings = Path.Combine(root, "user-settings.json");
        string projectSettings = Path.Combine(root, "project-settings.json");
        string pluginHooks = Path.Combine(root, "plugin-hooks.json");

        string message = HookDoctorTool.ExecuteCli(userSettings, projectSettings, pluginHooks, out int exitCode);

        Assert.Equal(
            "Grimora hooks: 0 in plugin, 0 direct, 0 overlapping, 0 direct only. Plugin enabled in settings: False. "
            + "No duplicate indicated by settings.",
            message);
        Assert.Equal(0, exitCode);
    }
}
