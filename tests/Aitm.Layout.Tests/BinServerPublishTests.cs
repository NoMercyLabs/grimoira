using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Xunit;

namespace Aitm.Layout.Tests;

// RESTRUCTURE.md sub-card 29e (slice 28 open point (a)): "SessionStartServerCheck.cs expects
// <plugin>/bin-server/Aitm.Server(.exe): make the name match exactly on Windows and Linux." build-server.ps1
// publishes Aitm.Server there, beside bin-cli/ (build-cli.ps1) and bin/ (build-mcp.ps1), and bin-server/
// is gitignored the same way. The published binary must actually start and answer /health.
public class BinServerPublishTests
{
    private static readonly string ExeName = OperatingSystem.IsWindows() ? "Aitm.Server.exe" : "Aitm.Server";

    [Fact]
    public void BuildServerScriptPublishesToBinServer()
    {
        string script = File.ReadAllText(Path.Combine(RepoPaths.Root, "build-server.ps1"));
        Assert.Contains(@"dotnet publish ""$PSScriptRoot/src/Aitm.Server/Aitm.Server.csproj""", script);
        Assert.Contains(@"-o ""$PSScriptRoot/bin-server""", script);
    }

    [Fact]
    public void GitIgnoresTheBinServerOutput()
    {
        string[] lines = File.ReadAllLines(Path.Combine(RepoPaths.Root, ".gitignore"));
        Assert.Contains("bin-server/", lines);
    }

    [Fact]
    public void BuildServerScriptClearsBinServerBeforePublishingSoNoStaleFilesRemain()
    {
        string script = File.ReadAllText(Path.Combine(RepoPaths.Root, "build-server.ps1"));
        int removeIndex = script.IndexOf("Remove-Item", StringComparison.Ordinal);
        int publishIndex = script.IndexOf(@"-o ""$PSScriptRoot/bin-server""", StringComparison.Ordinal);
        Assert.True(removeIndex >= 0,
            "build-server.ps1 never clears bin-server/ before publishing into it, so a stale earlier build's files remain.");
        Assert.True(publishIndex >= 0, "the bin-server publish line was not found; the assertion above needs updating to match its new shape.");
        Assert.True(removeIndex < publishIndex, "bin-server/ must be cleared before the publish that fills it back in, not after.");
    }

    [Fact]
    public async Task PublishedServerStartsAndAnswersHealthThenStops()
    {
        string exe = PublishedServerExe();
        string dataDir = Directory.CreateTempSubdirectory("aitm-bin-server-").FullName;
        int port = FreePort();
        Assert.NotEqual(7635, port);

        ProcessStartInfo psi = new(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.Environment["AITM_DATA_DIR"] = dataDir;
        psi.Environment["AITM_SERVER_PORT"] = port.ToString();

        using Process process = Process.Start(psi) ?? throw new InvalidOperationException($"could not start {exe}");
        try
        {
            using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(1) };
            bool healthy = false;
            Stopwatch sw = Stopwatch.StartNew();
            while (sw.Elapsed < TimeSpan.FromSeconds(15))
            {
                try
                {
                    using HttpResponseMessage response = await client.GetAsync($"http://127.0.0.1:{port}/health");
                    if (response.IsSuccessStatusCode) { healthy = true; break; }
                }
                catch (Exception) when (!process.HasExited)
                {
                    // still starting up
                }
                await Task.Delay(100);
            }
            Assert.True(healthy, "the published server never answered /health");
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(TimeSpan.FromSeconds(5));
            }
            Directory.Delete(dataDir, recursive: true);
        }
    }

    // verify.ps1 runs build-server.ps1 before `dotnet test`, so locally the exe must already be there; a
    // missing exe is the failure this test exists to catch. CI builds it its own way and never runs
    // build-server.ps1, so there (CI=true) the test publishes a private copy the same way.
    private static string PublishedServerExe()
    {
        string exe = Path.Combine(RepoPaths.Root, "bin-server", ExeName);
        if (File.Exists(exe)) return exe;
        if (Environment.GetEnvironmentVariable("CI") != "true")
            throw new InvalidOperationException($"{exe} not found: run build-server.ps1 first");
        return PrivateCopy.Value;
    }

    private static readonly Lazy<string> PrivateCopy = new(() =>
    {
        string outDir = Path.Combine(Path.GetTempPath(), "aitm-slice29e-server-publish-" + Guid.NewGuid().ToString("N"));
        ProcessStartInfo psi = new("dotnet",
            $"publish \"{Path.Combine(RepoPaths.Root, "src", "Aitm.Server", "Aitm.Server.csproj")}\" -c Release -o \"{outDir}\" -p:PublishAot=false")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using Process process = Process.Start(psi) ?? throw new InvalidOperationException("could not start dotnet publish");
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException($"publish failed:\n{stdout}\n{stderr}");
        return Path.Combine(outDir, ExeName);
    });

    private static int FreePort()
    {
        TcpListener l = new(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port == 7635 ? FreePort() : port;
    }
}
