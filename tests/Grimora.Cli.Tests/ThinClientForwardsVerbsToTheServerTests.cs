using System.Diagnostics;
using System.Text.Json;
using Grimora.Cli.Tools;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Grimora.Cli.Tests;

/// <summary>
/// RESTRUCTURE.md slice 29d, transport updated by Slice P1: Grimora.Cli is the thin client. Every verb except
/// `hook ...` and `server install-logon/uninstall-logon` goes to <c>POST /cli</c> over the local pipe /
/// Unix socket derived from <c>Grimora_DATA_DIR</c>, with <c>{args, cwd}</c> and the Claude-Project-Dir
/// header, and no token (Grimora holds no secret; only the current user can open the pipe); the answer's
/// stdout, stderr and exitCode are passed through exactly. The server here is a stand-in /cli bound to the
/// same pipe/socket a real Grimora.Server for this data dir would use, never the real 127.0.0.1:7635, so the
/// test pins what the client sends and prints, not what a verb does.
/// </summary>
public sealed class ThinClientForwardsVerbsToTheServerTests : IAsyncLifetime
{
    private readonly string _dataDir = Directory.CreateTempSubdirectory("grimora-thin-").FullName;
    private WebApplication? _server;
    private readonly List<(IHeaderDictionary Headers, string Body)> _calls = [];
    private object _answer = new { exitCode = 0, stdout = "", stderr = "" };

    public async Task InitializeAsync()
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        if (OperatingSystem.IsWindows())
        {
            builder.WebHost.ConfigureKestrel(o => o.ListenNamedPipe(ServerAddress.PipeName(_dataDir)));
        }
        else
        {
            builder.WebHost.ConfigureKestrel(o => o.ListenUnixSocket(ServerAddress.SocketPath(_dataDir)));
        }
        _server = builder.Build();
        _server.MapGet("/health", () => Results.Ok());
        _server.MapPost("/cli", async (HttpContext context) =>
        {
            string body = await new StreamReader(context.Request.Body).ReadToEndAsync();
            lock (_calls) _calls.Add((new HeaderDictionary(context.Request.Headers.ToDictionary(h => h.Key, h => h.Value)), body));
            return Results.Json(_answer);
        });
        await _server.StartAsync();
    }

    public async Task DisposeAsync()
    {
        if (_server is not null) await _server.DisposeAsync();
        try { Directory.Delete(_dataDir, recursive: true); } catch { /* best effort */ }
    }

    private Dictionary<string, string> Env(string? projectDir = "", string? instance = "") => new()
    {
        ["Grimora_SERVER_EXE"] = Path.Combine(_dataDir, "missing", "Grimora.Server.exe"),
        ["CLAUDE_PROJECT_DIR"] = projectDir ?? "",
        ["Grimora_INSTANCE"] = instance ?? "",
    };

    [Fact]
    public void AVerbRoundTripsStdoutStderrAndExitCodeExactlyAsCliAnswers()
    {
        _answer = new { exitCode = 7, stdout = "line one\nline two\n", stderr = "warn: from the server\n" };

        (string stdout, string stderr, int exit) = BuiltCli.Run(["todos", "--flag", "v"], _dataDir, Env());

        Assert.Equal(7, exit);
        Assert.Equal("line one\nline two\n", stdout);
        Assert.Equal("warn: from the server\n", stderr);
        (IHeaderDictionary _, string body) = Assert.Single(_calls);
        using JsonDocument doc = JsonDocument.Parse(body);
        Assert.Equal(["todos", "--flag", "v"], doc.RootElement.GetProperty("args").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(Directory.GetCurrentDirectory(), doc.RootElement.GetProperty("cwd").GetString());
    }

    [Fact]
    public void NoTokenIsSentEvenWithAnOldTokenFileOnlyTheProjectDirHeader()
    {
        File.WriteAllText(Path.Combine(_dataDir, "server.token"), "tok-thin-client\n");
        string projectDir = Path.Combine(Path.GetTempPath(), "test-thin-project");

        BuiltCli.Run(["help"], _dataDir, Env());
        BuiltCli.Run(["help"], _dataDir, Env(projectDir, "test-thin-inst"));

        Assert.Equal(2, _calls.Count);
        Assert.False(_calls[0].Headers.ContainsKey("Authorization"));
        Assert.Equal(Directory.GetCurrentDirectory(), _calls[0].Headers["Claude-Project-Dir"].ToString());
        Assert.False(_calls[0].Headers.ContainsKey("Grimora-Instance"));
        Assert.False(_calls[1].Headers.ContainsKey("Authorization"));
        Assert.Equal(projectDir, _calls[1].Headers["Claude-Project-Dir"].ToString());
        Assert.Equal("test-thin-inst", _calls[1].Headers["Grimora-Instance"].ToString());
    }

    [Fact]
    public void ServerDownAndNoServerExeExitsOneWithOneClearLine()
    {
        // A separate, empty data dir: nothing is bound to its derived pipe/socket, unlike _dataDir which
        // the stand-in server above is listening on.
        string downDataDir = Directory.CreateTempSubdirectory("grimora-thin-down-").FullName;
        try
        {
            Dictionary<string, string> env = Env();
            Stopwatch sw = Stopwatch.StartNew();

            (string stdout, string stderr, int exit) = BuiltCli.Run(["todos"], downDataDir, env);

            Assert.Equal(1, exit);
            Assert.Equal("", stdout);
            string line = Assert.Single(stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            Assert.Contains("grimora server", line);
            Assert.Contains(downDataDir, line);
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(15), $"took {sw.Elapsed}");
        }
        finally
        {
            Directory.Delete(downDataDir, recursive: true);
        }
    }

    [Fact]
    public void HookStillRunsLocallyWithTheServerDown()
    {
        string downDataDir = Directory.CreateTempSubdirectory("grimora-thin-down-").FullName;
        try
        {
            Dictionary<string, string> env = Env();

            (_, string hookErr, int hookExit) = BuiltCli.Run(["hook", "PreCompact"], downDataDir, env);

            Assert.Equal(0, hookExit);
            Assert.Equal("", hookErr);
            Assert.Empty(_calls);
        }
        finally
        {
            Directory.Delete(downDataDir, recursive: true);
        }
    }

    [Fact]
    public void TheCliAssemblyIsNamedGrimora()
    {
        Assert.Equal("grimora", typeof(ThinClient).Assembly.GetName().Name);
    }
}
