using System.Net;
using System.Text;
using System.Text.Json;
using Grimora.Facts.Tools;
using Grimora.Layout.Tests;
using Grimora.Store.Data;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Grimora.Server.Tests;

// RESTRUCTURE.md Slice P1: "`grimora mcp` is a stdio MCP server that serves the same 24 tools by forwarding each
// call to the service over the pipe". The service side: GET /tools lists the registry, POST /tools/{name}
// runs one tool under the same project resolution and store gate as /mcp.
public sealed class ToolsEndpointTests : IDisposable
{
    private static readonly Uri HttpBase = new("http://grimora-pipe.local/");
    private readonly string _dataDir = Directory.CreateTempSubdirectory("grimora-tools-endpoint-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dataDir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task ToolsListsExactlyTheGoldenToolsWithDescriptionAndInputSchema()
    {
        using RunningServer server = RunningServer.Start(_dataDir);
        using HttpClient client = server.CreateClient();

        using HttpResponseMessage response = await client.GetAsync("/tools");
        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement[] tools = [.. doc.RootElement.EnumerateArray()];
        Assert.Equal(26, tools.Length);
        Assert.Equal(GoldenListsTests.GoldenMcpTools.Append("chat_list").Append("chat_count").OrderBy(n => n), tools.Select(t => t.GetProperty("name").GetString()!).OrderBy(n => n));
        Assert.All(tools, t =>
        {
            Assert.False(string.IsNullOrWhiteSpace(t.GetProperty("description").GetString()));
            Assert.Equal(JsonValueKind.Object, t.GetProperty("inputSchema").ValueKind);
        });
    }

    [Fact]
    public async Task PostToolReturnsTheSameTextAsTheMcpEndpointForTheSameCall()
    {
        const string instance = "tools-parity";
        RunningServer.SeedInstance(_dataDir, instance, c => new AddTool().Execute(c, "tools-term", "", "manual", "tools-answer", "src", "", "stated"));
        using RunningServer server = RunningServer.Start(_dataDir);
        using HttpClient client = server.CreateClient();
        HttpClientTransportOptions options = new()
        {
            Endpoint = new Uri(HttpBase, "/mcp"),
            AdditionalHeaders = new Dictionary<string, string> { ["Grimora-Instance"] = instance },
        };
        await using McpClient mcp = await McpClient.CreateAsync(new HttpClientTransport(options, client));

        foreach (string query in new[] { "tools-term", "nothing-ever-matches-this-term-at-all" })
        {
            CallToolResult viaMcp = await mcp.CallToolAsync("history", new Dictionary<string, object?> { ["term"] = query });
            using HttpRequestMessage request = new(HttpMethod.Post, "/tools/history");
            request.Content = new StringContent(JsonSerializer.Serialize(new { term = query }), Encoding.UTF8, "application/json");
            request.Headers.Add("Grimora-Instance", instance);
            using HttpResponseMessage viaTools = await client.SendAsync(request);

            Assert.Equal(HttpStatusCode.OK, viaTools.StatusCode);
            Assert.Equal(((TextContentBlock)viaMcp.Content[0]).Text, await viaTools.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    public async Task PostToolResolvesTheProjectFromTheClaudeProjectDirHeader()
    {
        string projectDir = Path.Combine(Path.GetTempPath(), $"test-tools-project-{Guid.NewGuid():N}");
        string instance = StoreConnection.ResolveInstance(null, projectDir, Directory.GetCurrentDirectory());
        RunningServer.SeedInstance(_dataDir, instance, c => new AddTool().Execute(c, "dir-term", "", "manual", "dir-answer", "src", "", "stated"));
        using RunningServer server = RunningServer.Start(_dataDir);
        using HttpClient client = server.CreateClient();

        using HttpRequestMessage request = new(HttpMethod.Post, "/tools/history");
        request.Content = new StringContent("{\"term\":\"dir-term\"}", Encoding.UTF8, "application/json");
        request.Headers.Add("Claude-Project-Dir", projectDir);
        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Contains("dir-term", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task PostToolWithAnUnknownNameIsNotFoundAndBadArgumentsAreAnError()
    {
        using RunningServer server = RunningServer.Start(_dataDir);
        using HttpClient client = server.CreateClient();

        using HttpResponseMessage unknown = await client.PostAsync("/tools/no_such_tool", new StringContent("{}", Encoding.UTF8, "application/json"));
        using HttpResponseMessage bad = await client.PostAsync("/tools/history", new StringContent("{\"term\": [1,2]}", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.False(bad.IsSuccessStatusCode);
        Assert.False(string.IsNullOrWhiteSpace(await bad.Content.ReadAsStringAsync()));
    }
}
