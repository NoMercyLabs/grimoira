using System.Text.Json;
using Xunit;

namespace Aitm.Layout.Tests;

// RESTRUCTURE.md slice 28: the `aitm` entry in .mcp.json is the http entry for the server, with
// `headers: { "Claude-Project-Dir": "${CLAUDE_PROJECT_DIR}" }` plus `headersHelper` = `aitm server headers`
// (no shell script). A SessionStart hook starts the server when /health does not answer, through the
// built CLI (`aitm hook SessionStart`), never `dotnet run`.
public class McpJsonTests
{
    [Fact]
    public void AitmEntryIsTheHttpServerWithTheProjectHeaderAndTheHeadersHelper()
    {
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoPaths.Root, ".mcp.json")));
        JsonElement aitm = doc.RootElement.GetProperty("mcpServers").GetProperty("aitm");

        Assert.Equal("http", aitm.GetProperty("type").GetString());
        Assert.Equal("http://127.0.0.1:7635/mcp", aitm.GetProperty("url").GetString());
        JsonElement headers = aitm.GetProperty("headers");
        Assert.Equal("${CLAUDE_PROJECT_DIR}", headers.GetProperty("Claude-Project-Dir").GetString());
        Assert.False(headers.TryGetProperty("Authorization", out _), "the token comes from headersHelper, never the file");
        // headersHelper has no `args` field, so Claude Code always runs it in a shell
        // (code.claude.com/docs/en/mcp.md: "Claude Code runs the command in a shell and gives up on it
        // after 10 seconds"). A shell splits an unquoted ${CLAUDE_PLUGIN_ROOT} on a space, so the path
        // must be quoted the same way the docs quote their own shell-form examples. `aitm.exe` also does
        // not exist off Windows, so the portable, quote-safe form is `dotnet "<dll>" server headers`.
        string helper = aitm.GetProperty("headersHelper").GetString()!;
        Assert.Equal("dotnet \"${CLAUDE_PLUGIN_ROOT}/bin-cli/aitm.dll\" server headers", helper);
        Assert.False(aitm.TryGetProperty("command", out _));
    }

    [Fact]
    public void SessionStartRunsTheBuiltCliHook()
    {
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoPaths.Root, "hooks", "hooks.json")));
        JsonElement groups = doc.RootElement.GetProperty("hooks").GetProperty("SessionStart");
        JsonElement hook = Assert.Single(Assert.Single(groups.EnumerateArray()).GetProperty("hooks").EnumerateArray());

        // `args` is set, so this is exec form (code.claude.com/docs/en/hooks.md: "A command hook runs
        // as exec form when args is set... Set args whenever the hook references a path placeholder");
        // exec form spawns the executable directly with no shell, so ${CLAUDE_PLUGIN_ROOT} substitutes
        // as one argument even with a space in it and needs no quoting. `dotnet` plus the dll path is
        // also the portable form: `aitm.exe` does not exist off Windows, but `aitm.dll` and `dotnet` do.
        Assert.Equal("dotnet", hook.GetProperty("command").GetString());
        Assert.Equal(
            ["${CLAUDE_PLUGIN_ROOT}/bin-cli/aitm.dll", "hook", "SessionStart"],
            hook.GetProperty("args").EnumerateArray().Select(a => a.GetString()!).ToArray());
        Assert.True(hook.GetProperty("timeout").GetInt32() <= 20);
    }
}
