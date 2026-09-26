using System.Text.Json;
using Xunit;

namespace Aitm.Layout.Tests;

// RESTRUCTURE.md Slice P1: the `aitm` entry in .mcp.json is a stdio server: `node run-mcp.mjs`, which runs the
// published CLI's `mcp` verb (no http url, no headers, no headersHelper, nothing that carries a credential).
// A SessionStart hook starts the service when /health does not answer, through the built CLI.
public class McpJsonTests
{
    [Fact]
    public void AitmEntryIsAStdioServerThroughTheNoBuildNoNoiseStepWithNoHttpAndNoCredential()
    {
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoPaths.Root, ".mcp.json")));
        JsonElement aitm = doc.RootElement.GetProperty("mcpServers").GetProperty("aitm");

        Assert.Equal("node", aitm.GetProperty("command").GetString());
        Assert.Equal(
            ["${CLAUDE_PLUGIN_ROOT}/run-mcp.mjs"],
            [.. aitm.GetProperty("args").EnumerateArray().Select(a => a.GetString()!)]);
        Assert.True(File.Exists(Path.Combine(RepoPaths.Root, "run-mcp.mjs")));
        Assert.False(aitm.TryGetProperty("url", out _));
        Assert.False(aitm.TryGetProperty("headers", out _));
        Assert.False(aitm.TryGetProperty("headersHelper", out _));
        Assert.DoesNotContain("7635", File.ReadAllText(Path.Combine(RepoPaths.Root, ".mcp.json")));
    }

    [Fact]
    public void SessionStartRunsTheBuiltCliHook()
    {
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoPaths.Root, "hooks", "hooks.json")));
        JsonElement groups = doc.RootElement.GetProperty("hooks").GetProperty("SessionStart");
        JsonElement hook = Assert.Single(Assert.Single(groups.EnumerateArray()).GetProperty("hooks").EnumerateArray());

        // Slice 32a: SessionStart runs the Node step that builds the CLI when it is missing or stale and
        // otherwise runs `hook SessionStart` through it. Exec form (`args` set) needs no quoting.
        Assert.Equal("node", hook.GetProperty("command").GetString());
        Assert.Equal(
            ["${CLAUDE_PLUGIN_ROOT}/session-start.mjs"],
            [.. hook.GetProperty("args").EnumerateArray().Select(a => a.GetString()!)]);
        Assert.True(hook.GetProperty("timeout").GetInt32() <= 20);
    }
}
