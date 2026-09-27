using System.Diagnostics;
using System.Text.Json;
using Grimora.Layout.Tests;
using Xunit;
using Xunit.Abstractions;

namespace Grimora.Server.Tests;

// RESTRUCTURE.md "Phase 4, replaced (the owner, 2026-09-26)": the first call starts the service on its own,
// behind the scenes. Real Grimora.Server and grimora processes on a temp data dir (its own pipe/socket); the live
// service is never touched. Servers this test starts are stopped through their own POST /shutdown, never
// killed by a pid it did not start.
public sealed class OnDemandServiceTests : IDisposable
{
    private static readonly string Configuration = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd('/', '\\')).Parent!.Name;
    private static readonly string ServerDir = Path.Combine(RepoPaths.Root, "src", "Grimora.Server", "bin", Configuration, "net10.0");
    private static readonly string ServerExe = Path.Combine(ServerDir, OperatingSystem.IsWindows() ? "Grimora.Server.exe" : "Grimora.Server");
    private static readonly string CliDll = Path.Combine(RepoPaths.Root, "src", "Grimora.Cli", "bin", Configuration, "net10.0", "grimora.dll");

    private readonly ITestOutputHelper _output;
    private readonly string _dataDir = Directory.CreateTempSubdirectory("grimora-ondemand-").FullName;
    private readonly string _projectDir = Path.Combine(Path.GetTempPath(), $"test-ondemand-{Guid.NewGuid():N}");
    private readonly HashSet<int> _serversBefore = ServerPids();

    public OnDemandServiceTests(ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(_projectDir);
    }

    private static HashSet<int> ServerPids()
    {
        HashSet<int> pids = [];
        foreach (Process p in Process.GetProcessesByName("Grimora.Server")) { pids.Add(p.Id); p.Dispose(); }
        return pids;
    }

    private (int Exit, string Stdout, string Stderr) RunCli(string[] args, int idleSeconds = 0)
    {
        Assert.True(File.Exists(CliDll), $"grimora not built at {CliDll}");
        Assert.True(File.Exists(ServerExe), $"server not built at {ServerExe}");
        ProcessStartInfo psi = new("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
        psi.ArgumentList.Add(CliDll);
        foreach (string a in args) psi.ArgumentList.Add(a);
        psi.Environment["GRIMORA_DATA_DIR"] = _dataDir;
        psi.Environment["GRIMORA_SERVER_EXE"] = ServerExe;
        psi.Environment["CLAUDE_PROJECT_DIR"] = _projectDir;
        psi.Environment.Remove("GRIMORA_INSTANCE");
        if (idleSeconds > 0) psi.Environment["GRIMORA_IDLE_SECONDS"] = idleSeconds.ToString();
        using Process p = Process.Start(psi)!;
        p.StandardInput.Write("{}");
        p.StandardInput.Close();
        Task<string> stdout = p.StandardOutput.ReadToEndAsync();
        Task<string> stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(90000))
        {
            p.Kill(entireProcessTree: true);
            throw new TimeoutException("grimora did not exit within 90 s");
        }
        return (p.ExitCode, stdout.Result, stderr.Result);
    }

    private JsonDocument? Health()
    {
        try
        {
            using HttpClient client = PipeTestClient.CreateClient(_dataDir, TimeSpan.FromSeconds(1));
            using HttpResponseMessage r = client.GetAsync("/health").Result;
            return r.IsSuccessStatusCode ? JsonDocument.Parse(r.Content.ReadAsStringAsync().Result) : null;
        }
        catch { return null; }
    }

    [Fact]
    public void ManySimultaneousFirstCallsStartExactlyOneServerAndEveryCallIsAnswered()
    {
        const int calls = 6;
        (int Exit, string Stdout, string Stderr)[] results = new (int, string, string)[calls];
        Parallel.For(0, calls, new ParallelOptions { MaxDegreeOfParallelism = calls }, i => results[i] = RunCli(["help"]));

        for (int i = 0; i < calls; i++)
        {
            Assert.True(results[i].Exit == 0, $"call {i}: exit {results[i].Exit}; stderr {results[i].Stderr}");
            Assert.Contains("grimora <command>", results[i].Stdout);
            Assert.Equal("", results[i].Stderr);
        }

        Thread.Sleep(4000); // the losers of the single-instance race have exited by now
        using JsonDocument health = Health() ?? throw new InvalidOperationException("no server answers");
        int servingPid = health.RootElement.GetProperty("pid").GetInt32();
        List<int> mine = [.. ServerPids().Where(pid => !_serversBefore.Contains(pid))];
        _output.WriteLine($"{calls} first calls -> new Grimora.Server processes alive: [{string.Join(",", mine)}], serving pid {servingPid}");
        Assert.Equal([servingPid], mine);
    }

    [Fact]
    public void AHookWithTheServerDownStartsTheServerAndPrintsNothing()
    {
        (int exit, string stdout, string stderr) = RunCli(["hook", "PostToolUse"]);

        Assert.Equal(0, exit);
        Assert.Equal("", stdout);
        Assert.Equal("", stderr);
        Assert.NotNull(Health());
    }

    [Fact]
    public void ServiceStartIsSilentBesidesOneLineAndStartsTheServer()
    {
        (int exit, string stdout, string stderr) = RunCli(["service", "start"]);

        Assert.True(exit == 0, $"exit {exit}; stderr {stderr}");
        Assert.Equal("", stderr);
        Assert.Single(stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.NotNull(Health());
    }

    [Fact]
    public void ServiceStatusStartAndStopReportTheServiceStateInOneShortLine()
    {
        (int _, string down, string _) = RunCli(["service", "status"]);
        Assert.StartsWith("stopped", down.Trim());

        (int startExit, string _, string _) = RunCli(["service", "start"]);
        (int _, string up, string _) = RunCli(["service", "status"]);
        (int again, string _, string _) = RunCli(["service", "start"]); // idempotent
        Assert.Equal(0, startExit);
        Assert.Equal(0, again);
        Assert.StartsWith("running", up.Trim());
        Assert.Contains("idle exit 30 min", up);
        Assert.Single(up.Split('\n', StringSplitOptions.RemoveEmptyEntries));

        (int stopExit, string stopOut, string _) = RunCli(["service", "stop"]);
        Assert.Equal(0, stopExit);
        Assert.Single(stopOut.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.Null(Health());
    }

    [Fact]
    public void ARunningServerExitsByItselfAfterTheIdleTimeAndProbesDoNotKeepItAlive()
    {
        RunCli(["service", "start"], idleSeconds: 3);
        Stopwatch sw = Stopwatch.StartNew();
        // /health probes from clients must not reset the timer.
        while (sw.Elapsed < TimeSpan.FromSeconds(30) && Health() is { } h) { h.Dispose(); Thread.Sleep(300); }

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30), "the server never exited on its own");
        Assert.Null(Health());
    }

    [Fact]
    public void ACallResetsTheIdleTimerOfTheRunningServer()
    {
        RunCli(["service", "start"], idleSeconds: 4);
        Stopwatch sw = Stopwatch.StartNew();
        int calls = 0;
        while (sw.Elapsed < TimeSpan.FromSeconds(8))
        {
            RunCli(["help"]);
            calls++;
        }
        // Eight seconds of back-to-back calls, each under the 4 s idle time: still up.
        Assert.NotNull(Health());
        Assert.True(calls >= 2);
    }

    public void Dispose()
    {
        try
        {
            using HttpClient client = PipeTestClient.CreateClient(_dataDir, TimeSpan.FromSeconds(2));
            client.PostAsync("/shutdown", null).Wait(TimeSpan.FromSeconds(5));
        }
        catch (Exception) { /* nothing running */ }
        Stopwatch sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(10) && Health() is not null) Thread.Sleep(200);
        try { Directory.Delete(_dataDir, recursive: true); } catch (Exception) { }
        try { Directory.Delete(_projectDir, recursive: true); } catch (Exception) { }
    }
}
