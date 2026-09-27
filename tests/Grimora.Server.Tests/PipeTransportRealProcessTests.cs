using System.Diagnostics;
using Grimora.Layout.Tests;
using Xunit;

namespace Grimora.Server.Tests;

// RESTRUCTURE.md Slice P1: "no TCP listener remains (a test asserts it)"; "a pipe opened as the current
// user works". This runs the real published Grimora.Server process (never TestServer/WebApplicationFactory,
// which bypasses Kestrel) against a temp data dir and a per-test pipe/socket name, so it never touches
// ~/.grimora or the live 127.0.0.1:7635 service the coordinator left running. Client side: PipeTestClient.
public sealed class PipeTransportRealProcessTests : IDisposable
{
    private static readonly string Configuration = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd('/', '\\')).Parent!.Name;
    private static readonly string ServerDll = Path.Combine(RepoPaths.Root, "src", "Grimora.Server", "bin", Configuration, "net10.0", "Grimora.Server.dll");

    private readonly string _dataDir = Directory.CreateTempSubdirectory("grimora-pipe-real-").FullName;
    private Process? _server;

    private static async Task<HttpResponseMessage> GetHealthAsync(string dataDir, TimeSpan timeout)
    {
        using HttpClient client = PipeTestClient.CreateClient(dataDir, timeout);
        return await client.GetAsync("/health");
    }

    private async Task StartServerAsync()
    {
        Assert.True(File.Exists(ServerDll), $"Grimora.Server not built at {ServerDll}");
        ProcessStartInfo psi = new("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add(ServerDll);
        psi.Environment["Grimora_DATA_DIR"] = _dataDir;
        psi.Environment.Remove("CLAUDE_PROJECT_DIR");
        psi.Environment.Remove("Grimora_INSTANCE");
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
        using System.IO.Pipes.NamedPipeServerStream squatter = new(Grimora.Server.Data.ServerAddress.PipeName(_dataDir),
            System.IO.Pipes.PipeDirection.InOut, 4, System.IO.Pipes.PipeTransmissionMode.Byte, System.IO.Pipes.PipeOptions.Asynchronous);
        Assert.True(File.Exists(ServerDll), $"Grimora.Server not built at {ServerDll}");
        ProcessStartInfo psi = new("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add(ServerDll);
        psi.Environment["Grimora_DATA_DIR"] = _dataDir;
        _server = Process.Start(psi)!;

        bool exited = _server.WaitForExit(20000);
        await Task.CompletedTask;

        Assert.True(exited, "the server kept running although its pipe name was already taken");
        Assert.NotEqual(0, _server.ExitCode);
    }

    // Configuration can add a TCP endpoint behind Program.cs's back (Kestrel:Endpoints, ASPNETCORE_URLS,
    // HTTP_PORTS). The service must refuse to start rather than open one: fail loud, not ignore.
    [Theory]
    [InlineData("Kestrel__Endpoints__X__Url", "http://127.0.0.1:0")]
    [InlineData("ASPNETCORE_URLS", "http://127.0.0.1:0")]
    [InlineData("HTTP_PORTS", "0")]
    public async Task AConfiguredTcpEndpointMakesTheServerRefuseToStart(string variable, string value)
    {
        Assert.True(File.Exists(ServerDll), $"Grimora.Server not built at {ServerDll}");
        ProcessStartInfo psi = new("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add(ServerDll);
        psi.Environment["Grimora_DATA_DIR"] = _dataDir;
        psi.Environment[variable] = value;
        _server = Process.Start(psi)!;
        Task<string> stderr = _server.StandardError.ReadToEndAsync();
        Task<string> stdout = _server.StandardOutput.ReadToEndAsync();

        bool exited = _server.WaitForExit(20000);

        Assert.True(exited, $"the server kept running with {variable}={value}");
        Assert.NotEqual(0, _server.ExitCode);
        Assert.Contains("TCP", await stderr);
        _ = stdout;
    }

    // "Only the current user may open it", asserted on a LIVE pipe, not on source text. I cannot run as another
    // OS user, so this reads the pipe's real security descriptor and asserts who is allowed in.
    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public async Task TheLivePipeAllowsOnlyTheCurrentUser()
    {
        if (!OperatingSystem.IsWindows()) return; // the Unix twin below checks modes on a live socket
        await StartServerAsync();
        using System.IO.Pipes.NamedPipeClientStream client = new(".", Grimora.Server.Data.ServerAddress.PipeName(_dataDir),
            System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.CurrentUserOnly);
        await client.ConnectAsync(5000);

        System.IO.Pipes.PipeSecurity security = System.IO.Pipes.PipesAclExtensions.GetAccessControl(client);
        string me = System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;
        string[] seen = [.. security.GetAccessRules(true, false, typeof(System.Security.Principal.SecurityIdentifier))
            .Cast<System.IO.Pipes.PipeAccessRule>()
            .Where(r => r.AccessControlType == System.Security.AccessControl.AccessControlType.Allow)
            .Select(r => ((System.Security.Principal.SecurityIdentifier)r.IdentityReference).Value)
            .Distinct()];

        // Observed on the live pipe (Windows 10, .NET 10): exactly one Allow rule, the current user, FullControl.
        // Kestrel adds no SYSTEM or Administrators entry, so nothing beyond the user is tolerated.
        Assert.True(seen.SequenceEqual([me]), "the pipe allows more than the current user: " + string.Join(", ", seen));
    }

    [Fact]
    public async Task TheLiveUnixSocketIsUserOnly()
    {
        if (OperatingSystem.IsWindows()) return; // Windows has no modes; the ACL test above covers it
        await StartServerAsync();

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(_dataDir));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Grimora.Server.Data.ServerAddress.SocketPath(_dataDir)));
    }
}
