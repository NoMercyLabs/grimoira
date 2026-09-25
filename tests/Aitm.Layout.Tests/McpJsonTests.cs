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
        string helper = aitm.GetProperty("headersHelper").GetString()!;
        Assert.StartsWith("${CLAUDE_PLUGIN_ROOT}/bin-cli/", helper.Trim('"'));
        Assert.EndsWith("server headers", helper);
        Assert.False(aitm.TryGetProperty("command", out _));
    }

    [Fact]
    public void SessionStartRunsTheBuiltCliHook()
    {
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoPaths.Root, "hooks", "hooks.json")));
        JsonElement groups = doc.RootElement.GetProperty("hooks").GetProperty("SessionStart");
        JsonElement hook = Assert.Single(Assert.Single(groups.EnumerateArray()).GetProperty("hooks").EnumerateArray());

        Assert.Equal("${CLAUDE_PLUGIN_ROOT}/bin-cli/aitm.exe", hook.GetProperty("command").GetString());
        Assert.Equal(["hook", "SessionStart"], hook.GetProperty("args").EnumerateArray().Select(a => a.GetString()!).ToArray());
        Assert.True(hook.GetProperty("timeout").GetInt32() <= 20);
    }
}
