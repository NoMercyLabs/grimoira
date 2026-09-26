using System.Net;
using System.Text.Json;
using Aitm.Cli.Tools;
using Aitm.Facts.Tools;
using Aitm.Server.Data;
using Aitm.Store.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using ModelContextProtocol.Client;
using Xunit;

namespace Aitm.Server.Tests;

// RESTRUCTURE.md slice 28: `.mcp.json` sends `headers: { "Claude-Project-Dir": "${CLAUDE_PROJECT_DIR}" }`
// plus `headersHelper` = `aitm server headers`. These tests drive the in-process server (TestServer, never
// port 7635) with exactly what that helper prints (ServerHeadersCommand, linked into this project as
// source) and the project header, the way Claude Code combines the two.
public sealed class HeadersHelperMcpTests : IDisposable
{
    private readonly string _dataDir = Directory.CreateTempSubdirectory("aitm-hh-").FullName;
    private readonly string _projectsRoot = Directory.CreateTempSubdirectory("aitm-hh-proj-").FullName;
    private const string Port = "17638";
    private readonly string _allowedHost = $"127.0.0.1:{Port}";

    private WebApplicationFactory<Program> Factory()
    {
        Environment.SetEnvironmentVariable("AITM_DATA_DIR", _dataDir);
        Environment.SetEnvironmentVariable("AITM_SERVER_PORT", Port);
        return new WebApplicationFactory<Program>();
    }

    private Dictionary<string, string> HelperHeaders()
    {
        using StringWriter stdout = new();
        Assert.Equal(0, ServerHeadersCommand.Run(_dataDir, stdout));
        Dictionary<string, string> headers = JsonSerializer.Deserialize<Dictionary<string, string>>(stdout.ToString())!;
        return headers;
    }

    private async Task<McpClient> ConnectAsync(HttpClient httpClient, string projectDir)
    {
        Dictionary<string, string> headers = HelperHeaders();
        headers["Host"] = _allowedHost;
        headers[RequestProjectResolver.ProjectDirHeader] = projectDir;
        HttpClientTransportOptions options = new()
        {
            Endpoint = new Uri(httpClient.BaseAddress!, "/mcp"),
            AdditionalHeaders = headers,
        };
        return await McpClient.CreateAsync(new HttpClientTransport(options, httpClient));
    }

    private HttpClient Client(WebApplicationFactory<Program> factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri($"http://{_allowedHost}") });

    [Fact]
    public async Task HelperOutputGetsA200OnToolsList()
    {
        using WebApplicationFactory<Program> factory = Factory();
        using HttpClient httpClient = Client(factory);
        ServerToken.EnsureToken(_dataDir);

        using HttpRequestMessage request = ToolsListRequest();
        foreach ((string k, string v) in HelperHeaders()) request.Headers.TryAddWithoutValidation(k, v);
        HttpResponseMessage response = await httpClient.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task WithoutTheHelperOutputToolsListIs401()
    {
        using WebApplicationFactory<Program> factory = Factory();
        using HttpClient httpClient = Client(factory);
        ServerToken.EnsureToken(_dataDir);

        using HttpRequestMessage request = ToolsListRequest();
        HttpResponseMessage response = await httpClient.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task HelperOutputBeforeTheServerMadeATokenIsEmptyAndGets401()
    {
        Assert.Empty(HelperHeaders());
        using WebApplicationFactory<Program> factory = Factory();
        using HttpClient httpClient = Client(factory);
        _ = factory.Server; // the host creates server.token on start

        using HttpRequestMessage request = ToolsListRequest();
        HttpResponseMessage response = await httpClient.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Single(HelperHeaders()); // after start the helper finds the token
    }

    [Fact]
    public async Task TwoSessionsWithDifferentProjectDirHeadersResolveToDifferentProjects()
    {
        using WebApplicationFactory<Program> factory = Factory();
        ServerToken.EnsureToken(_dataDir);
        string projectA = Directory.CreateDirectory(Path.Combine(_projectsRoot, "HhAlpha")).FullName;
        string projectB = Directory.CreateDirectory(Path.Combine(_projectsRoot, "HhBeta")).FullName;
        SeedFact(StoreConnection.ResolveInstance(null, projectA, ""), "hhterm", "alpha-only-value");
        SeedFact(StoreConnection.ResolveInstance(null, projectB, ""), "hhterm", "beta-only-value");

        using HttpClient httpA = Client(factory);
        using HttpClient httpB = Client(factory);
        await using McpClient sessionA = await ConnectAsync(httpA, projectA);
        await using McpClient sessionB = await ConnectAsync(httpB, projectB);

        string a = await FactText(sessionA);
        string b = await FactText(sessionB);

        Assert.Contains("alpha-only-value", a);
        Assert.DoesNotContain("beta-only-value", a);
        Assert.Contains("beta-only-value", b);
        Assert.DoesNotContain("alpha-only-value", b);
    }

    private static async Task<string> FactText(McpClient client)
    {
        ModelContextProtocol.Protocol.CallToolResult result = await client.CallToolAsync("fact", new Dictionary<string, object?> { ["query"] = "hhterm" });
        return Assert.IsType<ModelContextProtocol.Protocol.TextContentBlock>(Assert.Single(result.Content)).Text;
    }

    private HttpRequestMessage ToolsListRequest()
    {
        HttpRequestMessage request = new(HttpMethod.Post, "/mcp") { Headers = { Host = _allowedHost } };
        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
        request.Content = new StringContent(
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"t","version":"1"}}}""",
            System.Text.Encoding.UTF8, "application/json");
        return request;
    }

    private void SeedFact(string instance, string term, string value)
    {
        Directory.CreateDirectory(Path.Combine(_dataDir, instance));
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
        try { Directory.Delete(_dataDir, recursive: true); } catch { /* best effort */ }
        try { Directory.Delete(_projectsRoot, recursive: true); } catch { /* best effort */ }
    }
}
