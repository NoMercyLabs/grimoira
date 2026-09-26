using System.Net;
using System.Text;
using System.Text.Json;
using Aitm.Server.Data;
using Aitm.Store.Data;
using Aitm.TestSupport;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using ModelContextProtocol.Client;
using Xunit;

namespace Aitm.Server.Tests;

// One rule for "which project" on /mcp, /cli and /hooks: explicit instance (--instance, Aitm-Instance)
// > Claude-Project-Dir header > the request's own cwd > the server's current directory. The server's own
// AITM_INSTANCE / CLAUDE_PROJECT_DIR are never read: a server started from one session's hook inherits
// them and would pin every other session to that project. Each request opens exactly one project store
// under the data dir, so the store directory it creates is the project it resolved.
public sealed class RequestProjectResolutionTests : IDisposable
{
    private readonly string _dataDir = Directory.CreateTempSubdirectory("aitm-resolve-").FullName;
    private readonly string _envInstance = $"test-envinst-{Guid.NewGuid():N}";
    private readonly string _envProjectDir = Path.Combine(Path.GetTempPath(), $"test-envdir-{Guid.NewGuid():N}");
    private readonly string _projectDir = Path.Combine(Path.GetTempPath(), $"test-reqdir-{Guid.NewGuid():N}");
    private readonly string? _savedInstance = Environment.GetEnvironmentVariable("AITM_INSTANCE");
    private readonly string? _savedProjectDir = Environment.GetEnvironmentVariable("CLAUDE_PROJECT_DIR");
    private const string Port = "17645";
    private readonly string _allowedHost = $"127.0.0.1:{Port}";

    public RequestProjectResolutionTests()
    {
        Directory.CreateDirectory(_envProjectDir);
        Directory.CreateDirectory(_projectDir);
        Environment.SetEnvironmentVariable("AITM_INSTANCE", _envInstance);
        Environment.SetEnvironmentVariable("CLAUDE_PROJECT_DIR", _envProjectDir);
    }

    private string RequestProject => Path.GetFileName(_projectDir);

    private WebApplicationFactory<Program> Factory()
    {
        Environment.SetEnvironmentVariable("AITM_DATA_DIR", _dataDir);
        Environment.SetEnvironmentVariable("AITM_SERVER_PORT", Port);
        return new WebApplicationFactory<Program>();
    }

    private HttpClient Client(WebApplicationFactory<Program> factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri($"http://{_allowedHost}") });

    private string[] Stores() => [.. Directory.GetDirectories(_dataDir).Select(d => Path.GetFileName(d)!)];

    private void AssertOnlyStore(string expected)
    {
        string[] stores = Stores();
        Assert.Equal([expected], stores);
        Assert.DoesNotContain(stores, s => s.Contains("envinst") || s.Contains("envdir"));
    }

    private HttpRequestMessage Request(string path, string token, string? projectDirHeader, string? instanceHeader, object body)
    {
        HttpRequestMessage request = new(HttpMethod.Post, path) { Headers = { Host = _allowedHost } };
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
        if (projectDirHeader is not null) request.Headers.TryAddWithoutValidation(McpInstanceContext.ProjectDirHeader, projectDirHeader);
        if (instanceHeader is not null) request.Headers.TryAddWithoutValidation(McpInstanceContext.InstanceHeader, instanceHeader);
        request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        return request;
    }

    private async Task PostCli(HttpClient client, string token, string? projectDirHeader, string? instanceHeader, string cwd)
    {
        using HttpRequestMessage request = Request("/cli", token, projectDirHeader, instanceHeader,
            new { args = new[] { "query", "anything" }, cwd });
        using HttpResponseMessage response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task PostHook(HttpClient client, string token, string? projectDirHeader, string? instanceHeader, string cwd)
    {
        using HttpRequestMessage request = Request("/hooks/PreCompact", token, projectDirHeader, instanceHeader,
            new { cwd, session_id = "resolve" });
        using HttpResponseMessage response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task CallMcp(HttpClient httpClient, string token, string? projectDirHeader)
    {
        Dictionary<string, string> headers = new()
        {
            ["Authorization"] = $"Bearer {token}",
            ["Host"] = _allowedHost,
        };
        if (projectDirHeader is not null) headers[McpInstanceContext.ProjectDirHeader] = projectDirHeader;
        HttpClientTransportOptions options = new() { Endpoint = new Uri(httpClient.BaseAddress!, "/mcp"), AdditionalHeaders = headers };
        await using McpClient client = await McpClient.CreateAsync(new HttpClientTransport(options, httpClient));
        await client.CallToolAsync("fact", new Dictionary<string, object?> { ["query"] = "anything" });
    }

    [Fact]
    public async Task McpWithAProjectDirHeaderUsesTheHeaderNotTheServerEnv()
    {
        using WebApplicationFactory<Program> factory = Factory();
        string token = ServerToken.EnsureToken(_dataDir);
        using HttpClient client = Client(factory);

        await CallMcp(client, token, _projectDir);

        AssertOnlyStore(RequestProject);
    }

    [Fact]
    public async Task McpWithoutHeadersUsesTheServerDirectoryNotTheServerEnv()
    {
        using WebApplicationFactory<Program> factory = Factory();
        string token = ServerToken.EnsureToken(_dataDir);
        using HttpClient client = Client(factory);

        await CallMcp(client, token, null);

        AssertOnlyStore(StoreConnection.ResolveInstance(null, null, Directory.GetCurrentDirectory()));
    }

    [Fact]
    public async Task CliWithAProjectDirHeaderUsesTheHeaderNotTheServerEnv()
    {
        using WebApplicationFactory<Program> factory = Factory();
        string token = ServerToken.EnsureToken(_dataDir);
        using HttpClient client = Client(factory);

        await PostCli(client, token, _projectDir, null, Path.GetTempPath());

        AssertOnlyStore(RequestProject);
    }

    [Fact]
    public async Task CliWithoutHeadersUsesTheBodyCwdNotTheServerEnv()
    {
        using WebApplicationFactory<Program> factory = Factory();
        string token = ServerToken.EnsureToken(_dataDir);
        using HttpClient client = Client(factory);

        await PostCli(client, token, null, null, _projectDir);

        AssertOnlyStore(RequestProject);
    }

    [Fact]
    public async Task HookWithAProjectDirHeaderUsesTheHeaderNotTheServerEnv()
    {
        using WebApplicationFactory<Program> factory = Factory();
        string token = ServerToken.EnsureToken(_dataDir);
        using HttpClient client = Client(factory);

        await PostHook(client, token, _projectDir, null, Path.GetTempPath());

        AssertOnlyStore(RequestProject);
    }

    [Fact]
    public async Task HookWithoutHeadersUsesThePayloadCwdNotTheServerEnv()
    {
        using WebApplicationFactory<Program> factory = Factory();
        string token = ServerToken.EnsureToken(_dataDir);
        using HttpClient client = Client(factory);

        await PostHook(client, token, null, null, _projectDir);

        AssertOnlyStore(RequestProject);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task CliAndHooksResolveTheSameProjectForTheSameInputs(bool sendProjectDir, bool sendInstance)
    {
        using WebApplicationFactory<Program> factory = Factory();
        string token = ServerToken.EnsureToken(_dataDir);
        using HttpClient client = Client(factory);
        string? projectDirHeader = sendProjectDir ? _projectDir : null;
        string? instanceHeader = sendInstance ? "test-headerinst" : null;
        string cwd = Path.GetTempPath();

        await PostCli(client, token, projectDirHeader, instanceHeader, cwd);
        string[] afterCli = Stores();
        await PostHook(client, token, projectDirHeader, instanceHeader, cwd);
        string[] afterHook = Stores();

        string expected = sendInstance ? "test-headerinst" : RequestProject;
        Assert.Equal([expected], afterCli);
        Assert.Equal([expected], afterHook);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("AITM_INSTANCE", _savedInstance);
        Environment.SetEnvironmentVariable("CLAUDE_PROJECT_DIR", _savedProjectDir);
        Environment.SetEnvironmentVariable("AITM_DATA_DIR", null);
        Environment.SetEnvironmentVariable("AITM_SERVER_PORT", null);
        SqliteConnection.ClearAllPools();
        // The hook handlers keep their own ~/.aitm/<instance> layout; remove anything this class could leave there.
        foreach (string name in new[] { _envInstance, Path.GetFileName(_envProjectDir), RequestProject, "test-headerinst" })
            AitmCliRunner.DeleteInstance(name);
        try { Directory.Delete(_dataDir, recursive: true); } catch { /* best effort cleanup */ }
        try { Directory.Delete(_envProjectDir, recursive: true); } catch { /* best effort cleanup */ }
        try { Directory.Delete(_projectDir, recursive: true); } catch { /* best effort cleanup */ }
    }
}
