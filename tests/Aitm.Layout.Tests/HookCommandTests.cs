using System.Text.Json;
using Xunit;

namespace Aitm.Layout.Tests;

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
                ? a.EnumerateArray().Select(x => x.GetString() ?? "").ToList()
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
                && command.Contains("${CLAUDE_PLUGIN_ROOT}")
                && !command.Contains("\"${CLAUDE_PLUGIN_ROOT}");
            if (hasUnquotedPlaceholder) offenders.Add($"{ev.Name}: {command}");
        }
        Assert.True(offenders.Count == 0, "shell-form hooks with an unquoted placeholder:\n" + string.Join('\n', offenders));
    }

    // The SessionStart hook uses exec form (`args` set): the safe way to reference `bin-cli/aitm.dll`,
    // since `aitm.exe` does not exist off Windows (only the Windows apphost is named `.exe`) and `dotnet`
    // plus the dll path is the one invocation that resolves the same way on every platform.
    [Fact]
    public void SessionStartInvokesThePortableDotnetDllForm()
    {
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoPaths.Root, "hooks", "hooks.json")));
        JsonElement hook = doc.RootElement.GetProperty("hooks").GetProperty("SessionStart")[0].GetProperty("hooks")[0];

        Assert.Equal("dotnet", hook.GetProperty("command").GetString());
        string[] args = hook.GetProperty("args").EnumerateArray().Select(a => a.GetString()!).ToArray();
        Assert.Equal("${CLAUDE_PLUGIN_ROOT}/bin-cli/aitm.dll", args[0]);
    }
}
