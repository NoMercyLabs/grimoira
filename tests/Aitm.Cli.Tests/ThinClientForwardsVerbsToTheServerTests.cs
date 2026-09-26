using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Aitm.Cli.Tools;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Aitm.Cli.Tests;

/// <summary>
/// RESTRUCTURE.md slice 29d: Aitm.Cli is the thin client. Every verb except `hook ...`, `server headers`
/// and `server install-logon/uninstall-logon` goes to <c>POST http://127.0.0.1:&lt;port&gt;/cli</c> with
/// <c>{args, cwd}</c>, the bearer token from the server token file and the Claude-Project-Dir header; the
/// answer's stdout, stderr and exitCode are passed through exactly. The server here is a stand-in /cli on
/// an ephemeral port (never 7635), so the test pins what the client sends and prints, not what a verb does.
/// </summary>
public sealed class ThinClientForwardsVerbsToTheServerTests : IAsyncLifetime
{
    private readonly string _dataDir = Directory.CreateTempSubdirectory("aitm-thin-").FullName;
    private WebApplication? _server;
    private int _port;
    private readonly List<(IHeaderDictionary Headers, string Body)> _calls = [];
    private object _answer = new { exitCode = 0, stdout = "", stderr = "" };

    public async Task InitializeAsync()
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        _server = builder.Build();
        _server.MapGet("/health", () => Results.Ok());
        _server.MapPost("/cli", async (HttpContext context) =>
        {
            string body = await new StreamReader(context.Request.Body).ReadToEndAsync();
            lock (_calls) _calls.Add((new HeaderDictionary(context.Request.Headers.ToDictionary(h => h.Key, h => h.Value)), body));
            return Results.Json(_answer);
        });
        await _server.StartAsync();
        _port = new Uri(_server.Urls.First()).Port;
        Assert.NotEqual(7635, _port);
    }

    public async Task DisposeAsync()
    {
        if (_server is not null) await _server.DisposeAsync();
        try { Directory.Delete(_dataDir, recursive: true); } catch { /* best effort */ }
    }

    private Dictionary<string, string> Env(string? projectDir = "", string? instance = "") => new()
    {
        ["AITM_SERVER_PORT"] = _port.ToString(),
        ["AITM_SERVER_EXE"] = Path.Combine(_dataDir, "missing", "Aitm.Server.exe"),
        ["CLAUDE_PROJECT_DIR"] = projectDir ?? "",
        ["AITM_INSTANCE"] = instance ?? "",
    };

    [Fact]
    public void AVerbRoundTripsStdoutStderrAndExitCodeExactlyAsCliAnswers()
    {
        _answer = new { exitCode = 7, stdout = "line one\nline two\n", stderr = "warn: from the server\n" };

        (string stdout, string stderr, int exit) = ServerHeadersCommandTests.RunBuiltCli(["todos", "--flag", "v"], _dataDir, Env());

        Assert.Equal(7, exit);
        Assert.Equal("line one\nline two\n", stdout);
        Assert.Equal("warn: from the server\n", stderr);
        (IHeaderDictionary _, string body) = Assert.Single(_calls);
        using JsonDocument doc = JsonDocument.Parse(body);
        Assert.Equal(["todos", "--flag", "v"], doc.RootElement.GetProperty("args").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(Directory.GetCurrentDirectory(), doc.RootElement.GetProperty("cwd").GetString());
    }

    [Fact]
    public void TheTokenFileIsReadAndSentWithTheProjectDirHeader()
    {
        File.WriteAllText(Path.Combine(_dataDir, "server.token"), "tok-thin-client\n");
        string projectDir = Path.Combine(Path.GetTempPath(), "test-thin-project");

        ServerHeadersCommandTests.RunBuiltCli(["help"], _dataDir, Env());
        ServerHeadersCommandTests.RunBuiltCli(["help"], _dataDir, Env(projectDir, "test-thin-inst"));

        Assert.Equal(2, _calls.Count);
        Assert.Equal("Bearer tok-thin-client", _calls[0].Headers.Authorization.ToString());
        Assert.Equal(Directory.GetCurrentDirectory(), _calls[0].Headers["Claude-Project-Dir"].ToString());
        Assert.False(_calls[0].Headers.ContainsKey("Aitm-Instance"));
        Assert.Equal("Bearer tok-thin-client", _calls[1].Headers.Authorization.ToString());
        Assert.Equal(projectDir, _calls[1].Headers["Claude-Project-Dir"].ToString());
        Assert.Equal("test-thin-inst", _calls[1].Headers["Aitm-Instance"].ToString());
    }

    [Fact]
    public void ServerDownAndNoServerExeExitsOneWithOneClearLine()
    {
        Dictionary<string, string> env = Env();
        env["AITM_SERVER_PORT"] = FreePort().ToString();
        Stopwatch sw = Stopwatch.StartNew();

        (string stdout, string stderr, int exit) = ServerHeadersCommandTests.RunBuiltCli(["todos"], _dataDir, env);

        Assert.Equal(1, exit);
        Assert.Equal("", stdout);
        string line = Assert.Single(stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        Assert.Contains("aitm server", line);
        Assert.Contains(env["AITM_SERVER_PORT"], line);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(15), $"took {sw.Elapsed}");
    }

    [Fact]
    public void HookAndServerHeadersStillRunLocallyWithTheServerDown()
    {
        Dictionary<string, string> env = Env();
        env["AITM_SERVER_PORT"] = FreePort().ToString();
        File.WriteAllText(Path.Combine(_dataDir, "server.token"), "tok-local");

        (string headers, string headersErr, int headersExit) = ServerHeadersCommandTests.RunBuiltCli(["server", "headers"], _dataDir, env);
        (_, string hookErr, int hookExit) = ServerHeadersCommandTests.RunBuiltCli(["hook", "PreCompact"], _dataDir, env);

        Assert.Equal(0, headersExit);
        Assert.Equal("""{"Authorization":"Bearer tok-local"}""", headers.Trim());
        Assert.Equal("", headersErr);
        Assert.Equal(0, hookExit);
        Assert.Equal("", hookErr);
        Assert.Empty(_calls);
    }

    [Fact]
    public void TheCliAssemblyIsNamedAitm()
    {
        Assert.Equal("aitm", typeof(ServerHeadersCommand).Assembly.GetName().Name);
    }

    private static int FreePort()
    {
        TcpListener l = new(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port == 7635 ? FreePort() : port;
    }
}
