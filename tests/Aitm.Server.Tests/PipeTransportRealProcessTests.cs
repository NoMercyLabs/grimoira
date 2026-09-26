using System.Diagnostics;
using Aitm.Layout.Tests;
using Xunit;

namespace Aitm.Server.Tests;

// RESTRUCTURE.md Slice P1: "no TCP listener remains (a test asserts it)"; "a pipe opened as the current
// user works". This runs the real published Aitm.Server process (never TestServer/WebApplicationFactory,
// which bypasses Kestrel) against a temp data dir and a per-test pipe/socket name, so it never touches
// ~/.aitm or the live 127.0.0.1:7635 service the coordinator left running. Client side: PipeTestClient.
public sealed class PipeTransportRealProcessTests : IDisposable
{
    private static readonly string Configuration = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd('/', '\\')).Parent!.Name;
    private static readonly string ServerDll = Path.Combine(RepoPaths.Root, "src", "Aitm.Server", "bin", Configuration, "net10.0", "Aitm.Server.dll");

    private readonly string _dataDir = Directory.CreateTempSubdirectory("aitm-pipe-real-").FullName;
    private Process? _server;

    private static async Task<HttpResponseMessage> GetHealthAsync(string dataDir, TimeSpan timeout)
    {
        using HttpClient client = PipeTestClient.CreateClient(dataDir, timeout);
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

    // Pipe squatting: if something already owns the pipe name, a server started afterwards must fail loudly,
    // not quietly share or lose the name to the squatter. Kestrel's named-pipe listener must create the
    // FIRST instance.
    [Fact]
    public async Task AServerStartedAfterASquatterOnThePipeNameFailsLoudly()
    {
        if (!OperatingSystem.IsWindows()) return;
        using System.IO.Pipes.NamedPipeServerStream squatter = new(Aitm.Server.Data.ServerAddress.PipeName(_dataDir),
            System.IO.Pipes.PipeDirection.InOut, 4, System.IO.Pipes.PipeTransmissionMode.Byte, System.IO.Pipes.PipeOptions.Asynchronous);
        Assert.True(File.Exists(ServerDll), $"Aitm.Server not built at {ServerDll}");
        ProcessStartInfo psi = new("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add(ServerDll);
        psi.Environment["AITM_DATA_DIR"] = _dataDir;
        _server = Process.Start(psi)!;

        bool exited = _server.WaitForExit(20000);
        await Task.CompletedTask;

        Assert.True(exited, "the server kept running although its pipe name was already taken");
        Assert.NotEqual(0, _server.ExitCode);
    }
}
