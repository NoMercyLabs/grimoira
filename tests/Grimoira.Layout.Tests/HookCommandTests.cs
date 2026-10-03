extern alias cli;

using System.Text.Json;
using Xunit;

namespace Grimoira.Layout.Tests;

// A hook runs on every prompt, edit or session end, so its start cost is paid again and again.
// `dotnet run` builds the project before it runs: about 3 s warm, and past the 10 s timeout on a
// fresh plugin install that has no build output yet. A hook command must start a built program.
public class HookCommandTests
{
    [Fact]
    public void NoHookBuildsBeforeItRuns()
    {
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoPaths.Root, "hooks", "hooks.json")));
        List<string> offenders = [];
        foreach (JsonProperty ev in doc.RootElement.GetProperty("hooks").EnumerateObject())
        foreach (JsonElement group in ev.Value.EnumerateArray())
        foreach (JsonElement hook in group.GetProperty("hooks").EnumerateArray())
        {
            string command = hook.TryGetProperty("command", out JsonElement c) ? c.GetString() ?? "" : "";
            List<string> args = hook.TryGetProperty("args", out JsonElement a)
                ? [.. a.EnumerateArray().Select(x => x.GetString() ?? "")]
                : [];
            bool builds = (command == "dotnet" && args.FirstOrDefault() is "run" or "build")
                || command.StartsWith("dotnet run", StringComparison.Ordinal);
            if (builds) offenders.Add($"{ev.Name}: {command} {string.Join(' ', args)}");
        }
        Assert.True(offenders.Count == 0, "hooks that build before they run:\n" + string.Join('\n', offenders));
    }

    // RESTRUCTURE.md sub-card 29e (slice 28 open point (b)): "${CLAUDE_PLUGIN_ROOT} with a space breaks
    // the unquoted command." code.claude.com/docs/en/hooks.md: "A command hook runs as exec form when
    // args is set, and shell form when args is omitted... Set args whenever the hook references a path
    // placeholder, since each element is passed as one argument with no quoting" and separately "In
    // shell form, wrap each placeholder in double quotes." So every hook `command` string that itself
    // (not just its args) contains a path placeholder must either have `args` set (exec form, safe as-is)
    // or quote the placeholder (shell form).
    [Fact]
    public void EveryPlaceholderInAShellFormCommandIsQuoted()
    {
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoPaths.Root, "hooks", "hooks.json")));
        List<string> offenders = [];
        foreach (JsonProperty ev in doc.RootElement.GetProperty("hooks").EnumerateObject())
        foreach (JsonElement group in ev.Value.EnumerateArray())
        foreach (JsonElement hook in group.GetProperty("hooks").EnumerateArray())
        {
            string command = hook.TryGetProperty("command", out JsonElement c) ? c.GetString() ?? "" : "";
            bool execForm = hook.TryGetProperty("args", out _);
            bool hasUnquotedPlaceholder = !execForm
                && ((command.Contains("${CLAUDE_PLUGIN_ROOT}") && !command.Contains("\"${CLAUDE_PLUGIN_ROOT}"))
                    || (command.Contains("${CLAUDE_PLUGIN_DATA}") && !command.Contains("\"${CLAUDE_PLUGIN_DATA}")));
            if (hasUnquotedPlaceholder) offenders.Add($"{ev.Name}: {command}");
        }
        Assert.True(offenders.Count == 0, "shell-form hooks with an unquoted placeholder:\n" + string.Join('\n', offenders));
    }

    // A fresh install has no bin-cli/ (build output is gitignored), so SessionStart runs the file-based app
    // bootstrap.cs first. It builds the CLI and server into ${CLAUDE_PLUGIN_DATA} when they are missing or
    // stale, and otherwise runs `hook SessionStart` through the built CLI. Exec form (`args` set) keeps a path
    // with a space as one argument.
    [Fact]
    public void SessionStartRunsTheBootstrapAppFirst()
    {
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoPaths.Root, "hooks", "hooks.json")));
        JsonElement hook = doc.RootElement.GetProperty("hooks").GetProperty("SessionStart")[0].GetProperty("hooks")[0];

        Assert.Equal("dotnet", hook.GetProperty("command").GetString());
        string[] args = [.. hook.GetProperty("args").EnumerateArray().Select(a => a.GetString()!)];
        Assert.Equal(["${CLAUDE_PLUGIN_ROOT}/bootstrap.cs"], args);
        Assert.True(File.Exists(Path.Combine(RepoPaths.Root, "bootstrap.cs")));
    }

    // RESTRUCTURE.md slice 32a: "Every other hook slot and the headersHelper point at
    // ${CLAUDE_PLUGIN_DATA}/bin-cli/grimoira.dll", now through the `current` build folder so a rebuild never writes
    // into a folder in use. The plugin root has no build output after an install.
    [Fact]
    public void NoHookSlotRunsTheCliFromThePluginRoot()
    {
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoPaths.Root, "hooks", "hooks.json")));
        List<string> offenders = [];
        foreach (JsonProperty ev in doc.RootElement.GetProperty("hooks").EnumerateObject())
        foreach (JsonElement group in ev.Value.EnumerateArray())
        foreach (JsonElement hook in group.GetProperty("hooks").EnumerateArray())
        {
            string line = string.Join(' ', ArgsOf(hook).Prepend(hook.GetProperty("command").GetString() ?? ""));
            if (line.Contains("${CLAUDE_PLUGIN_ROOT}/bin-", StringComparison.Ordinal)) offenders.Add($"{ev.Name}: {line}");
        }
        Assert.True(offenders.Count == 0, "hook slots that run build output from the plugin root:\n" + string.Join('\n', offenders));
    }

    // The PreCompact, UserPromptSubmit, PostToolUse and SessionEnd slots run `grimoira hook <event>` from the
    // built CLI in the plugin data folder, in exec form with no shell and no Node. While the first build runs
    // there is no dll: dotnet prints its "Could not execute" text and exits 1 (documented in README.md).
    // SessionEnd is one slot: the server's /hooks/SessionEnd runs all three SessionEnd handlers.
    [Theory]
    [InlineData("PreCompact")]
    [InlineData("UserPromptSubmit")]
    [InlineData("PostToolUse")]
    [InlineData("SessionEnd")]
    [InlineData("Stop")]
    public void TheEventRunsTheHookVerbOfThePublishedCli(string eventName)
    {
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoPaths.Root, "hooks", "hooks.json")));
        JsonElement group = Assert.Single(doc.RootElement.GetProperty("hooks").GetProperty(eventName).EnumerateArray());
        JsonElement hook = Assert.Single(group.GetProperty("hooks").EnumerateArray());

        Assert.Equal("dotnet", hook.GetProperty("command").GetString());
        string[] args = [.. hook.GetProperty("args").EnumerateArray().Select(a => a.GetString()!)];
        Assert.Equal(["${CLAUDE_PLUGIN_DATA}/current/bin-cli/grimoira.dll", "hook", eventName], args);
    }

    // One PostToolUse slot serves both handlers: the server picks IndexOnEdit or PatternWatch by tool_name,
    // so the matcher must name every tool either side wants (the edits, and the repeated searches the
    // pattern counter learns from).
    [Fact]
    public void ThePostToolUseMatcherCoversEditsAndSearches()
    {
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoPaths.Root, "hooks", "hooks.json")));
        JsonElement group = Assert.Single(doc.RootElement.GetProperty("hooks").GetProperty("PostToolUse").EnumerateArray());

        Assert.Equal("Write|Edit|MultiEdit|NotebookEdit|Read|Grep|Glob|Bash", group.GetProperty("matcher").GetString());
        Assert.Equal(65, group.GetProperty("hooks")[0].GetProperty("timeout").GetInt32());
    }

    // The SessionEnd handlers index for seconds. Claude Code gives SessionEnd hooks a shared 1.5 s budget
    // that a plugin's own timeout does not raise; only an async command hook is not cut at it.
    [Fact]
    public void TheSessionEndSlotStaysAsync()
    {
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoPaths.Root, "hooks", "hooks.json")));
        JsonElement hook = doc.RootElement.GetProperty("hooks").GetProperty("SessionEnd")[0].GetProperty("hooks")[0];

        Assert.True(hook.GetProperty("async").GetBoolean());
    }

    // A forwarded hook ends at its own deadline (HookForwarder.Deadlines), because an async command hook's
    // timeout is not enforced in an interactive session. The deadline must never outlast the slot that runs
    // the event's handlers: the slot whose args run `hook <event>`.
    [Fact]
    public void EachForwardedHookDeadlineIsNotAboveItsSlotTimeout()
    {
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoPaths.Root, "hooks", "hooks.json")));
        Assert.NotEmpty(cli::Grimoira.Cli.Tools.HookForwarder.Deadlines);
        foreach ((string eventName, TimeSpan deadline) in cli::Grimoira.Cli.Tools.HookForwarder.Deadlines)
        {
            JsonElement slot = doc.RootElement.GetProperty("hooks").GetProperty(eventName).EnumerateArray()
                .SelectMany(group => group.GetProperty("hooks").EnumerateArray())
                .Single(hook => RunsHookVerb(hook, eventName));
            int timeoutSeconds = slot.GetProperty("timeout").GetInt32();
            Assert.True(deadline <= TimeSpan.FromSeconds(timeoutSeconds),
                $"{eventName}: deadline {deadline.TotalSeconds} s is above its slot timeout {timeoutSeconds} s");
        }
    }

    private static string[] ArgsOf(JsonElement hook) =>
        hook.TryGetProperty("args", out JsonElement a) ? [.. a.EnumerateArray().Select(x => x.GetString() ?? "")] : [];

    private static bool RunsHookVerb(JsonElement hook, string eventName) =>
        ArgsOf(hook) is [.., "hook", { } last] && last == eventName;
}
