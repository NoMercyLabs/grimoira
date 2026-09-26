using System.Net;
using System.Net.Http.Json;
using Aitm.Server.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Aitm.Server.Tests;

// RESTRUCTURE.md "Slice 25: Aitm.Server host." — a Kestrel host bound to 127.0.0.1:7635 only, that
// rejects a wrong Host header and any Origin header. It holds no secret and checks no token
// (the owner, 2026-09-26). These tests run the real Program pipeline through an in-process
// TestServer (WebApplicationFactory<Program>), never a real socket bind on 7635 — Program reads its
// data directory and port from AITM_DATA_DIR / AITM_SERVER_PORT so a test never touches ~/.aitm.
public sealed class ServerHostTests : IDisposable
{
    private readonly string _dataDir = Directory.CreateTempSubdirectory("aitm-server-host-").FullName;
    private const string Port = "17635"; // never the real 7635, and TestServer never binds a socket anyway.
    private readonly string _allowedHost = $"127.0.0.1:{Port}";

    private WebApplicationFactory<Program> Factory()
    {
        Environment.SetEnvironmentVariable("AITM_DATA_DIR", _dataDir);
        Environment.SetEnvironmentVariable("AITM_SERVER_PORT", Port);
        return new WebApplicationFactory<Program>();
    }

    private static HttpRequestMessage Request(HttpMethod method, string path, string host, string? bearer = null, bool addOrigin = false)
    {
        HttpRequestMessage request = new(method, path) { Headers = { Host = host } };
        if (bearer is not null) request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearer);
        if (addOrigin) request.Headers.TryAddWithoutValidation("Origin", "http://evil.example");
        return request;
    }

    [Fact]
    public async Task HealthWithoutAuthReportsVersionUptimeAndOpenStores()
    {
        using WebApplicationFactory<Program> factory = Factory();
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.SendAsync(Request(HttpMethod.Get, "/health", _allowedHost));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Dictionary<string, object>? body = await response.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        Assert.NotNull(body);
        Assert.True(body!.ContainsKey("version"));
        Assert.True(body.ContainsKey("uptimeSeconds"));
        Assert.True(body.ContainsKey("openStores"));
        Assert.True(body.ContainsKey("buildStamp")); // null outside a published build (slice 32b)
    }

    // AITM holds no secret (the owner, 2026-09-26): the guard is the loopback bind, the Host check and the Origin
    // refusal, never a token. A plain loopback call with no Authorization header is served.
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("localhost")]
    public async Task ALoopbackCallWithoutAnyTokenIsServed(string hostName)
    {
        using WebApplicationFactory<Program> factory = Factory();
        using HttpClient client = factory.CreateClient();
        using HttpRequestMessage request = Request(HttpMethod.Post, "/hooks/NoSuchEvent", $"{hostName}:{Port}");
        request.Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");

        HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task AnAuthorizationHeaderIsIgnoredNotChecked()
    {
        using WebApplicationFactory<Program> factory = Factory();
        using HttpClient client = factory.CreateClient();
        using HttpRequestMessage request = Request(HttpMethod.Post, "/hooks/NoSuchEvent", _allowedHost, bearer: "left-over-from-an-old-client");
        request.Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");

        HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task WrongHostHeaderIsRefusedOnEveryRoute()
    {
        using WebApplicationFactory<Program> factory = Factory();
        using HttpClient client = factory.CreateClient();

        foreach (string path in new[] { "/mcp", "/cli", "/hooks/PreCompact" })
        {
            HttpResponseMessage response = await client.SendAsync(Request(HttpMethod.Post, path, $"attacker.example:{Port}"));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
    }

    [Fact]
    public async Task AnyOriginHeaderIsRefusedOnEveryRoute()
    {
        using WebApplicationFactory<Program> factory = Factory();
        using HttpClient client = factory.CreateClient();

        foreach (string path in new[] { "/mcp", "/cli", "/hooks/PreCompact" })
        {
            HttpResponseMessage response = await client.SendAsync(Request(HttpMethod.Post, path, _allowedHost, addOrigin: true));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
    }

    [Fact]
    public void TheServerListensOnLoopbackOnly()
    {
        string program = File.ReadAllText(Path.Combine(Aitm.Layout.Tests.RepoPaths.Root, "src", "Aitm.Server", "Data", "Program.cs"));

        Assert.Contains("builder.WebHost.UseUrls($\"http://127.0.0.1:{port}\");", program);
        Assert.DoesNotContain("0.0.0.0", program);
        Assert.DoesNotContain("ListenAnyIP", program);
    }

    [Fact]
    public async Task WrongHostHeaderIsRefusedEvenForHealth()
    {
        using WebApplicationFactory<Program> factory = Factory();
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.SendAsync(Request(HttpMethod.Get, "/health", "evil.example"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AnyOriginHeaderIsRefusedEvenForHealth()
    {
        using WebApplicationFactory<Program> factory = Factory();
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.SendAsync(Request(HttpMethod.Get, "/health", _allowedHost, addOrigin: true));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public void StartingTheServerWritesNoTokenFile()
    {
        using WebApplicationFactory<Program> factory = Factory();
        _ = factory.Server;

        Assert.False(File.Exists(Path.Combine(_dataDir, "server.token")), "the server wrote a token file");
    }

    // A server.token left by an older build is the user's file: ignored, never deleted.
    [Fact]
    public void AnOldTokenFileIsLeftAlone()
    {
        string path = Path.Combine(_dataDir, "server.token");
        File.WriteAllText(path, "old-token");
        using WebApplicationFactory<Program> factory = Factory();
        _ = factory.Server;

        Assert.Equal("old-token", File.ReadAllText(path));
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("AITM_DATA_DIR", null);
        Environment.SetEnvironmentVariable("AITM_SERVER_PORT", null);
        try { Directory.Delete(_dataDir, recursive: true); } catch { /* best effort cleanup */ }
    }
}
