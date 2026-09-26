using System.Net;
using Aitm.Facts.Tools;
using Aitm.Layout.Tests;
using Aitm.Server.Data;
using Aitm.Store.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using ModelContextProtocol.Client;
using Xunit;

namespace Aitm.Server.Tests;

// RESTRUCTURE.md "Slice 26: /mcp on the server, beside mcp.cs." The 25 golden MCP tools served over the
// real ASP.NET pipeline (WebApplicationFactory<Program>), behind the same bearer-auth middleware
// ServerHostTests already proves for every non-/health route.
public sealed class McpEndpointTests : IDisposable
{
    private readonly string _dataDir = Directory.CreateTempSubdirectory("aitm-mcp-").FullName;
    private const string Port = "17636";
    private readonly string _allowedHost = $"127.0.0.1:{Port}";

    private WebApplicationFactory<Program> Factory()
    {
        Environment.SetEnvironmentVariable("AITM_DATA_DIR", _dataDir);
        Environment.SetEnvironmentVariable("AITM_SERVER_PORT", Port);
        return new WebApplicationFactory<Program>();
    }

    private async Task<McpClient> ConnectAsync(HttpClient httpClient, string instance)
    {
        HttpClientTransportOptions options = new()
        {
            Endpoint = new Uri(httpClient.BaseAddress!, "/mcp"),
            AdditionalHeaders = new Dictionary<string, string>
            {
                ["Host"] = _allowedHost,
                [RequestProjectResolver.InstanceHeader] = instance,
            },
        };
        return await McpClient.CreateAsync(new HttpClientTransport(options, httpClient));
    }

    [Fact]
    public async Task ToolsListMatchesTheGoldenMcpList()
    {
        using WebApplicationFactory<Program> factory = Factory();
        using HttpClient httpClient = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri($"http://{_allowedHost}") });

        await using McpClient client = await ConnectAsync(httpClient, "tools-list-instance");
        IList<McpClientTool> tools = await client.ListToolsAsync();

        string[] names = [.. tools.Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal)];
        string[] expected = [.. GoldenListsTests.GoldenMcpTools.OrderBy(n => n, StringComparer.Ordinal)];
        Assert.Equal(expected, names);
    }

    [Fact]
    public async Task RequestWithoutATokenIs401()
    {
        using WebApplicationFactory<Program> factory = Factory();
        using HttpClient httpClient = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri($"http://{_allowedHost}") });

        using HttpRequestMessage request = new(HttpMethod.Post, "/mcp") { Headers = { Host = _allowedHost } };
        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
        request.Content = new StringContent(
            """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""",
            System.Text.Encoding.UTF8, "application/json");

        HttpResponseMessage response = await httpClient.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task FactToolMatchesThePhase2PinnedOutputForASeededTerm()
    {
        using WebApplicationFactory<Program> factory = Factory();
        const string instance = "fact-mcp-instance";
        SeedFact(instance, "mcpfixtureterm", "mcpfixturevalue");

        using HttpClient httpClient = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri($"http://{_allowedHost}") });
        await using McpClient client = await ConnectAsync(httpClient, instance);

        ModelContextProtocol.Protocol.CallToolResult result = await client.CallToolAsync("fact", new Dictionary<string, object?> { ["query"] = "mcpfixtureterm" });

        string text = Assert.IsType<ModelContextProtocol.Protocol.TextContentBlock>(Assert.Single(result.Content)).Text;
        Assert.Contains("mcpfixturevalue", text);
    }

    [Fact]
    public async Task TwoSessionsWritingToTheSameProjectStoreBackToBackAndConcurrentlyNeverSeeALockError()
    {
        using WebApplicationFactory<Program> factory = Factory();
        const string instance = "concurrency-instance";

        using HttpClient httpClientA = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri($"http://{_allowedHost}") });
        using HttpClient httpClientB = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri($"http://{_allowedHost}") });
        await using McpClient sessionA = await ConnectAsync(httpClientA, instance);
        await using McpClient sessionB = await ConnectAsync(httpClientB, instance);

        // Back to back.
        await CallLearn(sessionA, "k1");
        await CallLearn(sessionB, "k2");

        // Concurrently, against the exact same project store.
        Task<ModelContextProtocol.Protocol.CallToolResult> t1 = CallLearn(sessionA, "k3").AsTask();
        Task<ModelContextProtocol.Protocol.CallToolResult> t2 = CallLearn(sessionB, "k4").AsTask();
        ModelContextProtocol.Protocol.CallToolResult[] results = await Task.WhenAll(t1, t2);

        foreach (ModelContextProtocol.Protocol.CallToolResult r in results)
        {
            Assert.NotEqual(true, r.IsError);
            string text = Assert.IsType<ModelContextProtocol.Protocol.TextContentBlock>(Assert.Single(r.Content)).Text;
            Assert.DoesNotContain("database is locked", text, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static ValueTask<ModelContextProtocol.Protocol.CallToolResult> CallLearn(McpClient client, string key) =>
        client.CallToolAsync("brain_learn", new Dictionary<string, object?>
        {
            ["kind"] = "node",
            ["key"] = key,
            ["a"] = "fact",
            ["b"] = "mcp concurrency fixture",
            ["c"] = "seeded by McpEndpointTests",
        });

    private void SeedFact(string instance, string term, string value)
    {
        string dbPath = Path.Combine(_dataDir, instance, "aitm.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        ProjectStore seedStore = new(_dataDir);
        try
        {
            SqliteConnection connection = seedStore.Acquire(instance).Connection;
            new AddTool().Execute(connection, term, "", "manual", value, "test-fixture", "", "stated");
        }
        finally
        {
            seedStore.Dispose();
        }
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("AITM_DATA_DIR", null);
        Environment.SetEnvironmentVariable("AITM_SERVER_PORT", null);
        try { Directory.Delete(_dataDir, recursive: true); } catch { /* best effort cleanup */ }
    }
}
