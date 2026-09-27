using System.Text.Json;
using Xunit;

namespace Grimora.Layout.Tests;

// RESTRUCTURE.md Slice P1: the `grimora` entry in .mcp.json is a stdio server: `dotnet <data>/current/bin-cli/grimora.dll mcp`, the
// published CLI's `mcp` verb (no http url, no headers, no headersHelper, nothing that carries a credential).
// A SessionStart hook starts the service when /health does not answer, through the built CLI.
public class McpJsonTests
{
    [Fact]
    public void GrimoraEntryIsAStdioServerThroughThePublishedCliWithNoHttpAndNoCredential()
    {
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoPaths.Root, ".mcp.json")));
        JsonElement grimora = doc.RootElement.GetProperty("mcpServers").GetProperty("grimora");

        Assert.Equal("dotnet", grimora.GetProperty("command").GetString());
        Assert.Equal(
            ["${CLAUDE_PLUGIN_DATA}/current/bin-cli/grimora.dll", "mcp"],
            [.. grimora.GetProperty("args").EnumerateArray().Select(a => a.GetString()!)]);
        Assert.False(grimora.TryGetProperty("url", out _));
        Assert.False(grimora.TryGetProperty("headers", out _));
        Assert.False(grimora.TryGetProperty("headersHelper", out _));
        Assert.DoesNotContain("7635", File.ReadAllText(Path.Combine(RepoPaths.Root, ".mcp.json")));
    }

    [Fact]
    public void SessionStartRunsTheBuiltCliHook()
    {
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoPaths.Root, "hooks", "hooks.json")));
        JsonElement groups = doc.RootElement.GetProperty("hooks").GetProperty("SessionStart");
        JsonElement hook = Assert.Single(Assert.Single(groups.EnumerateArray()).GetProperty("hooks").EnumerateArray());

        // SessionStart runs bootstrap.cs, which builds the CLI when it is missing or stale and otherwise runs
        // `hook SessionStart` through it. Exec form (`args` set) needs no quoting.
        Assert.Equal("dotnet", hook.GetProperty("command").GetString());
        Assert.Equal(
            ["${CLAUDE_PLUGIN_ROOT}/bootstrap.cs"],
            [.. hook.GetProperty("args").EnumerateArray().Select(a => a.GetString()!)]);
        Assert.True(hook.GetProperty("timeout").GetInt32() <= 20);
    }
}
