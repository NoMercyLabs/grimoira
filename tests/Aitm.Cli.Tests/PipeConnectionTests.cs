using Aitm.Cli.Tools;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Xunit;

namespace Aitm.Cli.Tests;

// RESTRUCTURE.md Slice P1: "the thin client, HookForwarder, ServerAutoStart and the 32b hand-over talk
// over the pipe" through ONE shared connection helper. PipeConnection.CreateClient hands back an
// HttpClient whose SocketsHttpHandler.ConnectCallback opens a NamedPipeClientStream (Windows) or a Unix
// domain socket (macOS/Linux) instead of a TCP socket. This test proves the helper's connect callback
// actually reaches a listener on that transport — a minimal Kestrel host bound the same way Aitm.Server
// will be (ListenNamedPipe / ListenUnixSocket), never the real 127.0.0.1:7635 service.
public sealed class PipeConnectionTests : IDisposable
{
    private readonly string _dataDir = Directory.CreateTempSubdirectory("aitm-pipe-conn-").FullName;
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

    [Fact]
    public async Task TwoDifferentDataDirsNeverCrossTalk()
    {
        await StartListenerAsync();
        string otherDataDir = Directory.CreateTempSubdirectory("aitm-pipe-conn-other-").FullName;
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
}
