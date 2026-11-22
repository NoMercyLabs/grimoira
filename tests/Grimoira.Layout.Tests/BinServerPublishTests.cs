extern alias cli;

using System.Diagnostics;
using Xunit;

namespace Grimoira.Layout.Tests;

// RESTRUCTURE.md sub-card 29e (slice 28 open point (a)): "SessionStartServerCheck.cs expects
// <plugin>/bin-server/Grimoira.Server(.exe): make the name match exactly on Windows and Linux." build-server.ps1
// publishes Grimoira.Server there, beside bin-cli/ (build-cli.ps1) and bin/ (build-mcp.ps1), and bin-server/
// is gitignored the same way. The published binary must actually start and answer /health, over its
// local pipe/socket (Slice P1), never a TCP port.
public class BinServerPublishTests
{
    private static readonly string ExeName = OperatingSystem.IsWindows() ? "Grimoira.Server.exe" : "Grimoira.Server";

    [Fact]
    public void BuildServerScriptPublishesToBinServer()
    {
        string script = File.ReadAllText(Path.Combine(RepoPaths.Root, "build-server.ps1"));
        Assert.Contains(@"dotnet publish ""$PSScriptRoot/src/Grimoira.Server/Grimoira.Server.csproj""", script);
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
        string dataDir = Directory.CreateTempSubdirectory("grimoira-bin-server-").FullName;

        ProcessStartInfo psi = new(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            Environment = { ["GRIMOIRA_DATA_DIR"] = dataDir },
        };

        using Process process = Process.Start(psi) ?? throw new InvalidOperationException($"could not start {exe}");
        try
        {
            bool healthy = false;
            Stopwatch sw = Stopwatch.StartNew();
            while (sw.Elapsed < TimeSpan.FromSeconds(15))
            {
                try
                {
                    using HttpClient client = cli::Grimoira.Cli.Tools.PipeConnection.CreateClient(dataDir, TimeSpan.FromSeconds(1));
                    using HttpResponseMessage response = await client.GetAsync("/health");
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

    // 2026-11-22: Sdk.Web turns on Server GC. On a 32-thread dev box the idle server then held 7.3 GB of
    // committed heap for 32 MB of live objects (dotnet-gcdump), and the PC ran out of RAM. One user, one
    // local process: the published server must use workstation GC.
    [Fact]
    public void PublishedServerUsesWorkstationGc()
    {
        string config = Path.Combine(Path.GetDirectoryName(PublishedServerExe())!, "Grimoira.Server.runtimeconfig.json");
        using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(config));
        System.Text.Json.JsonElement props = doc.RootElement.GetProperty("runtimeOptions").GetProperty("configProperties");
        Assert.True(props.TryGetProperty("System.GC.Server", out System.Text.Json.JsonElement server), "runtimeconfig has no System.GC.Server entry");
        Assert.False(server.GetBoolean(), "the published server runs Server GC; it must use workstation GC");
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
        string outDir = Path.Combine(Path.GetTempPath(), "grimoira-slice29e-server-publish-" + Guid.NewGuid().ToString("N"));
        ProcessStartInfo psi = new("dotnet",
            $"publish \"{Path.Combine(RepoPaths.Root, "src", "Grimoira.Server", "Grimoira.Server.csproj")}\" -c Release -o \"{outDir}\" -p:PublishAot=false")
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
}
