using System.Net;
using System.Net.Http.Json;
using Aitm.Server.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Aitm.Server.Tests;

// RESTRUCTURE.md "Slice 25: Aitm.Server host." — a Kestrel host bound to 127.0.0.1:7635 only, that
// rejects a wrong Host header and any Origin header, serves /health without auth, and requires a
// bearer token on every other route. These tests run the real Program pipeline through an in-process
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
    }

    [Fact]
    public async Task OtherRoutesRejectMissingOrWrongToken()
    {
        string token = ServerToken.EnsureToken(_dataDir);
        using WebApplicationFactory<Program> factory = Factory();
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage noToken = await client.SendAsync(Request(HttpMethod.Get, "/anything", _allowedHost));
        Assert.Equal(HttpStatusCode.Unauthorized, noToken.StatusCode);

        HttpResponseMessage wrongToken = await client.SendAsync(Request(HttpMethod.Get, "/anything", _allowedHost, bearer: token + "x"));
        Assert.Equal(HttpStatusCode.Unauthorized, wrongToken.StatusCode);

        HttpResponseMessage rightToken = await client.SendAsync(Request(HttpMethod.Get, "/anything", _allowedHost, bearer: token));
        Assert.NotEqual(HttpStatusCode.Unauthorized, rightToken.StatusCode);
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
    public void TokenFileIsUserOnlyAndStableAcrossRestarts()
    {
        string first = ServerToken.EnsureToken(_dataDir);
        string second = ServerToken.EnsureToken(_dataDir); // simulates a restart reading the same file.

        Assert.Equal(first, second);

        string path = Path.Combine(_dataDir, ServerToken.FileName);
        Assert.True(File.Exists(path));
        if (!OperatingSystem.IsWindows())
        {
            UnixFileMode mode = File.GetUnixFileMode(path);
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
        }
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("AITM_DATA_DIR", null);
        Environment.SetEnvironmentVariable("AITM_SERVER_PORT", null);
        try { Directory.Delete(_dataDir, recursive: true); } catch { /* best effort cleanup */ }
    }
}
