using System.IO.Pipes;
using Grimoira.Cli.Tools;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Xunit;

namespace Grimoira.Cli.Tests;

// RESTRUCTURE.md Slice P1: "the thin client, HookForwarder, ServerAutoStart and the 32b hand-over talk
// over the pipe" through ONE shared connection helper. PipeConnection.CreateClient hands back an
// HttpClient whose SocketsHttpHandler.ConnectCallback opens a NamedPipeClientStream (Windows) or a Unix
// domain socket (macOS/Linux) instead of a TCP socket. This test proves the helper's connect callback
// actually reaches a listener on that transport — a minimal Kestrel host bound the same way Grimoira.Server
// will be (ListenNamedPipe / ListenUnixSocket), never the real 127.0.0.1:7635 service.
public sealed class PipeConnectionTests : IDisposable
{
    private readonly string _dataDir = Directory.CreateTempSubdirectory("grimoira-pipe-conn-").FullName;
    private WebApplication? _app;

    private async Task<WebApplication> StartListenerAsync()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        if (OperatingSystem.IsWindows())
        {
            builder.WebHost.ConfigureKestrel(o => o.ListenNamedPipe(ServerAddress.PipeName(_dataDir)));
        }
        else
        {
            builder.WebHost.ConfigureKestrel(o => o.ListenUnixSocket(ServerAddress.SocketPath(_dataDir)));
        }
        WebApplication app = builder.Build();
        app.MapGet("/ping", () => "pong");
        await app.StartAsync();
        _app = app;
        return app;
    }

    [Fact]
    public async Task CreateClientReachesAListenerOnTheDerivedPipeOrSocket()
    {
        await StartListenerAsync();
        using HttpClient client = PipeConnection.CreateClient(_dataDir, TimeSpan.FromSeconds(5));

        string body = await client.GetStringAsync("/ping");

        Assert.Equal("pong", body);
    }

    // ThinClient starts the server only when the first call fails as HttpRequestError.ConnectionError (the
    // TCP "connection refused" it used to get). A pipe/socket with nobody behind it must fail the same way,
    // not as the TaskCanceledException a connect timeout would otherwise surface as, or a down server is
    // never started on demand.
    [Fact]
    public async Task ADataDirWithNoServerFailsAsAConnectionError()
    {
        using HttpClient client = PipeConnection.CreateClient(_dataDir, TimeSpan.FromMilliseconds(300), TimeSpan.FromSeconds(30));

        HttpRequestException ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("/ping"));

        Assert.Equal(HttpRequestError.ConnectionError, ex.HttpRequestError);
    }

    [Fact]
    public async Task TwoDifferentDataDirsNeverCrossTalk()
    {
        await StartListenerAsync();
        string otherDataDir = Directory.CreateTempSubdirectory("grimoira-pipe-conn-other-").FullName;
        try
        {
            using HttpClient client = PipeConnection.CreateClient(otherDataDir, TimeSpan.FromMilliseconds(300));
            await Assert.ThrowsAnyAsync<Exception>(() => client.GetStringAsync("/ping"));
        }
        finally
        {
            try { Directory.Delete(otherDataDir, recursive: true); } catch { /* best effort cleanup */ }
        }
    }

    public void Dispose()
    {
        _app?.StopAsync().GetAwaiter().GetResult();
        _app?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        try { Directory.Delete(_dataDir, recursive: true); } catch { /* best effort cleanup */ }
    }

    // Pipe squatting: another user who creates the pipe first must not receive our requests. With
    // CurrentUserOnly the client refuses a pipe owned by anyone else. I cannot run as a second OS user, so
    // this asserts the option is passed and that a pipe made by the same user still connects.
    [Fact]
    public void TheClientOpensThePipeWithCurrentUserOnly()
    {
        Assert.True(PipeConnection.ClientPipeOptions.HasFlag(PipeOptions.CurrentUserOnly));
        Assert.True(PipeConnection.ClientPipeOptions.HasFlag(PipeOptions.Asynchronous));
    }

    [Fact]
    public async Task APipeMadeByTheSameUserStillConnects()
    {
        if (!OperatingSystem.IsWindows()) return;
        using NamedPipeServerStream pipe = new(ServerAddress.PipeName(_dataDir), PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        Task accepted = pipe.WaitForConnectionAsync();
        using HttpClient client = PipeConnection.CreateClient(_dataDir, TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(700));

        await Assert.ThrowsAnyAsync<Exception>(() => client.GetAsync("/ping")); // the stand-in never speaks HTTP

        Assert.Same(accepted, await Task.WhenAny(accepted, Task.Delay(2000))); // the client connected to the same-user pipe
    }
}
