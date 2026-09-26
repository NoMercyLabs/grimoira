using System.Diagnostics;
using System.IO.Pipes;
using System.Net.Sockets;
using Aitm.Layout.Tests;
using Aitm.Server.Data;
using Xunit;

namespace Aitm.Server.Tests;

// RESTRUCTURE.md Slice P1: "no TCP listener remains (a test asserts it)"; "a pipe opened as the current
// user works". This runs the real published Aitm.Server process (never TestServer/WebApplicationFactory,
// which bypasses Kestrel) against a temp data dir and a per-test pipe/socket name, so it never touches
// ~/.aitm or the live 127.0.0.1:7635 service the coordinator left running.
//
// The client side here is a minimal inline connect, not Aitm.Cli.Tools.PipeConnection: Aitm.Server.Tests
// references Aitm.Cli for build order only (ReferenceOutputAssembly="false", HooksEndpointTests's comment
// — its linked Hooks copies would clash with Aitm.Hooks), so the assembly's types are not usable here.
public sealed class PipeTransportRealProcessTests : IDisposable
{
    private static readonly string Configuration = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd('/', '\\')).Parent!.Name;
    private static readonly string ServerDll = Path.Combine(RepoPaths.Root, "src", "Aitm.Server", "bin", Configuration, "net10.0", "Aitm.Server.dll");

    private readonly string _dataDir = Directory.CreateTempSubdirectory("aitm-pipe-real-").FullName;
    private Process? _server;

    private static async Task<HttpResponseMessage> GetHealthAsync(string dataDir, TimeSpan timeout)
    {
        using SocketsHttpHandler handler = new()
        {
            ConnectCallback = async (_, ct) =>
            {
                if (OperatingSystem.IsWindows())
                {
                    NamedPipeClientStream pipe = new(".", ServerAddress.PipeName(dataDir), PipeDirection.InOut, PipeOptions.Asynchronous);
                    await pipe.ConnectAsync((int)timeout.TotalMilliseconds, ct);
                    return pipe;
                }
                Socket socket = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(ServerAddress.SocketPath(dataDir)), ct);
                return new NetworkStream(socket, ownsSocket: true);
            },
            ConnectTimeout = timeout,
        };
        using HttpClient client = new(handler) { BaseAddress = new Uri("http://aitm-pipe.local/"), Timeout = timeout };
        return await client.GetAsync("/health");
    }

    private async Task StartServerAsync()
    {
        Assert.True(File.Exists(ServerDll), $"Aitm.Server not built at {ServerDll}");
        ProcessStartInfo psi = new("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add(ServerDll);
        psi.Environment["AITM_DATA_DIR"] = _dataDir;
        psi.Environment.Remove("CLAUDE_PROJECT_DIR");
        psi.Environment.Remove("AITM_INSTANCE");
        _server = Process.Start(psi)!;

        Stopwatch sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(20))
        {
            try
            {
                using HttpResponseMessage response = await GetHealthAsync(_dataDir, TimeSpan.FromSeconds(1));
                if (response.IsSuccessStatusCode) return;
            }
            catch (Exception) when (!_server.HasExited) { }
            await Task.Delay(150);
        }
        throw new InvalidOperationException("the test server never answered /health over the pipe");
    }

    [Fact]
    public async Task HealthAnswersOverThePipeAsTheCurrentUser()
    {
        await StartServerAsync();

        using HttpResponseMessage response = await GetHealthAsync(_dataDir, TimeSpan.FromSeconds(5));

        Assert.True(response.IsSuccessStatusCode);
    }

    [Fact]
    public async Task TheRunningServerProcessOpensNoTcpListeningPort()
    {
        if (!OperatingSystem.IsWindows())
        {
            // TCP-listener enumeration below is implemented for Windows (netstat) only; the dev box this
            // slice was built on is Windows. The Unix ListenUnixSocket path has no live check here.
            return;
        }
        await StartServerAsync();

        ProcessStartInfo netstat = new("netstat", "-ano -p TCP") { UseShellExecute = false, RedirectStandardOutput = true };
        using Process probe = Process.Start(netstat)!;
        string output = await probe.StandardOutput.ReadToEndAsync();
        probe.WaitForExit(5000);

        string pid = _server!.Id.ToString();
        IEnumerable<string> listeningLinesForThisProcess = output
            .Split('\n')
            .Where(line => line.Contains("LISTENING") && line.TrimEnd().EndsWith(pid));

        Assert.Empty(listeningLinesForThisProcess);
    }

    public void Dispose()
    {
        if (_server is { HasExited: false })
        {
            try { _server.Kill(entireProcessTree: true); _server.WaitForExit(5000); } catch { /* best effort */ }
        }
        _server?.Dispose();
        try { Directory.Delete(_dataDir, recursive: true); } catch { /* best effort cleanup */ }
    }
}
