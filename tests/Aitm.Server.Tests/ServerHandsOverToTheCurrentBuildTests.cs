using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Aitm.Layout.Tests;
using Microsoft.Data.Sqlite;
using Xunit;
using Xunit.Abstractions;

namespace Aitm.Server.Tests;

// RESTRUCTURE.md slice 32b: a new build's server takes over from the running one. The plugin data folder holds
// builds/<stamp12>/{bin-cli,bin-server} and a `current` link (slice 32a). Here two copies of this build's CLI and
// server output stand in for build A and build B, told apart only by the stamp in bin-cli/build-stamp.txt. A
// server from build A runs on a free port with a temp data dir while `current` points at build B; then
// `dotnet current/bin-cli/aitm.dll hook SessionStart` runs, as session-start.mjs runs it.
public sealed class ServerHandsOverToTheCurrentBuildTests : IClassFixture<ServerHandsOverToTheCurrentBuildTests.TwoBuilds>, IDisposable
{
    private readonly TwoBuilds _builds;
    private readonly ITestOutputHelper _output;
    private readonly string _dataDir = Directory.CreateTempSubdirectory("aitm-handover-").FullName;
    private readonly string _projectDir = Path.Combine(Path.GetTempPath(), $"test-handover-{Guid.NewGuid():N}");
    private readonly int _port = FreePort();
    private readonly List<Process> _servers = [];

    public ServerHandsOverToTheCurrentBuildTests(TwoBuilds builds, ITestOutputHelper output)
    {
        _builds = builds;
        _output = output;
        Directory.CreateDirectory(_projectDir);
    }

    [Fact]
    public void AfterSessionStartTheRunningServerIsTheCurrentBuild()
    {
        Process oldServer = StartServer(_builds.StampA);
        Assert.Equal(_builds.StampA, HealthStamp());

        (int exit, string stdout, string stderr) = RunSessionStart();

        Assert.True(exit == 0, $"exit {exit}; stderr {stderr}");
        Assert.Equal("", stdout);
        Assert.Equal(_builds.StampB, HealthStamp());
        Assert.True(oldServer.WaitForExit(10000), "the build A server did not exit");
        Assert.Equal(0, oldServer.ExitCode);
    }

    [Fact]
    public async Task ACallInFlightOnTheOldServerFinishesBeforeItExits()
    {
        Process oldServer = StartServer(_builds.StampA);
        Assert.Equal(0, PostCli(["add", "--term", "handover-warmup"]).Exit);
        string db = Directory.GetFiles(_dataDir, "aitm.db", SearchOption.AllDirectories).Single();

        Task<(int Exit, string Stdout, string Stderr)> inFlight;
        Task<(int Exit, string Stdout, string Stderr)> sessionStart;
        using (SqliteConnection holder = new($"Data Source={db};Pooling=False"))
        {
            holder.Open();
            using (SqliteCommand begin = holder.CreateCommand())
            {
                begin.CommandText = "BEGIN IMMEDIATE";
                begin.ExecuteNonQuery(); // the call below waits on this write lock: it is in flight on build A
            }
            inFlight = Task.Run(() => PostCli(["add", "--term", "handover-in-flight"]));
            Thread.Sleep(1000);
            Assert.False(inFlight.IsCompleted, "the call was not held in flight");
            sessionStart = Task.Run(RunSessionStart);
            Thread.Sleep(1500);
            Assert.False(oldServer.HasExited, "the build A server exited with a call in flight");
            using SqliteCommand rollback = holder.CreateCommand();
            rollback.CommandText = "ROLLBACK";
            rollback.ExecuteNonQuery();
        }

        (int exit, string stdout, string stderr) = await inFlight;
        Assert.True(exit == 0, $"the in-flight call answered exit {exit}; stderr {stderr}");
        Assert.Contains("handover-in-flight", stdout);
        Assert.Equal(0, (await sessionStart).Exit);
        Assert.Equal(_builds.StampB, HealthStamp());
        Assert.True(oldServer.WaitForExit(10000), "the build A server did not exit");
    }

    [Fact]
    public async Task TwoSessionStartsDuringOneSwapStartOneServer()
    {
        StartServer(_builds.StampA);
        HashSet<int> started = [];
        using CancellationTokenSource stop = new();
        Task watcher = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                foreach (int pid in ServersFromTheBuildFolder()) started.Add(pid);
                Thread.Sleep(20);
            }
        });

        Task<(int Exit, string Stdout, string Stderr)> first = Task.Run(RunSessionStart);
        Task<(int Exit, string Stdout, string Stderr)> second = Task.Run(RunSessionStart);
        Assert.Equal(0, (await first).Exit);
        Assert.Equal(0, (await second).Exit);
        Thread.Sleep(1000);
        stop.Cancel();
        await watcher;

        Assert.Equal(_builds.StampB, HealthStamp());
        Assert.True(started.Count == 1, $"servers started from the builds: {started.Count} ({string.Join(", ", started)})");
    }

    private Process StartServer(string stamp)
    {
        ProcessStartInfo psi = new("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add(Path.Combine(_builds.BuildDir(stamp), "bin-server", "Aitm.Server.dll"));
        psi.Environment["AITM_DATA_DIR"] = _dataDir;
        psi.Environment["AITM_SERVER_PORT"] = _port.ToString();
        psi.Environment.Remove("CLAUDE_PROJECT_DIR");
        psi.Environment.Remove("AITM_INSTANCE");
        Process process = Process.Start(psi)!;
        process.OutputDataReceived += (_, _) => { };
        process.ErrorDataReceived += (_, _) => { };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        _servers.Add(process);
        Stopwatch sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(20))
        {
            if (Health() is not null) return process;
            Assert.False(process.HasExited, "the test server exited before it answered /health");
            Thread.Sleep(100);
        }
        throw new InvalidOperationException("the test server never answered /health");
    }

    private JsonElement? Health()
    {
        try
        {
            using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(1) };
            using HttpResponseMessage response = client.GetAsync($"http://127.0.0.1:{_port}/health").Result;
            if (!response.IsSuccessStatusCode) return null;
            using JsonDocument doc = JsonDocument.Parse(response.Content.ReadAsStringAsync().Result);
            return doc.RootElement.Clone();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private string? HealthStamp()
    {
        JsonElement? health = Health();
        Assert.True(health is not null, "no server answers /health");
        return health.Value.TryGetProperty("buildStamp", out JsonElement stamp) ? stamp.GetString() : null;
    }

    private (int Exit, string Stdout, string Stderr) PostCli(string[] args)
    {
        using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(60) };
        using HttpRequestMessage request = new(HttpMethod.Post, $"http://127.0.0.1:{_port}/cli");
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {File.ReadAllText(Path.Combine(_dataDir, "server.token")).Trim()}");
        request.Headers.TryAddWithoutValidation("Claude-Project-Dir", _projectDir);
        request.Content = new StringContent(JsonSerializer.Serialize(new { args, cwd = _projectDir }), Encoding.UTF8, "application/json");
        using HttpResponseMessage response = client.Send(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument doc = JsonDocument.Parse(response.Content.ReadAsStringAsync().Result);
        return (doc.RootElement.GetProperty("exitCode").GetInt32(), doc.RootElement.GetProperty("stdout").GetString()!, doc.RootElement.GetProperty("stderr").GetString()!);
    }

    private (int Exit, string Stdout, string Stderr) RunSessionStart()
    {
        ProcessStartInfo psi = new("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
        psi.ArgumentList.Add(Path.Combine(_builds.Current, "bin-cli", "aitm.dll"));
        psi.ArgumentList.Add("hook");
        psi.ArgumentList.Add("SessionStart");
        psi.Environment["AITM_DATA_DIR"] = _dataDir;
        psi.Environment["AITM_SERVER_PORT"] = _port.ToString();
        psi.Environment.Remove("AITM_SERVER_EXE"); // the server beside the CLI: current/bin-server
        psi.Environment.Remove("CLAUDE_PROJECT_DIR");
        psi.Environment.Remove("AITM_INSTANCE");
        using Process p = Process.Start(psi)!;
        p.StandardInput.Close();
        Task<string> stdout = p.StandardOutput.ReadToEndAsync();
        Task<string> stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(30000))
        {
            p.Kill(entireProcessTree: true);
            throw new TimeoutException("aitm hook SessionStart did not exit within 30 s");
        }
        _output.WriteLine($"SessionStart exit {p.ExitCode}; stderr: {stderr.Result}");
        return (p.ExitCode, stdout.Result, stderr.Result);
    }

    /// <summary>Aitm.Server processes started from the two builds (the CLI starts the apphost; the test runs A
    /// through dotnet).</summary>
    private List<int> ServersFromTheBuildFolder()
    {
        List<int> pids = [];
        foreach (Process p in Process.GetProcessesByName("Aitm.Server"))
        {
            try
            {
                if (p.MainModule?.FileName.StartsWith(_builds.Root, StringComparison.OrdinalIgnoreCase) == true) pids.Add(p.Id);
            }
            catch (Exception) { /* gone, or not ours */ }
            finally { p.Dispose(); }
        }
        return pids;
    }

    public void Dispose()
    {
        foreach (Process p in _servers)
        {
            try { if (!p.HasExited) { p.Kill(entireProcessTree: true); p.WaitForExit(5000); } } catch (Exception) { }
            p.Dispose();
        }
        foreach (int pid in ServersFromTheBuildFolder())
        {
            try
            {
                using Process p = Process.GetProcessById(pid);
                p.Kill(entireProcessTree: true);
                p.WaitForExit(5000);
            }
            catch (Exception) { }
        }
        try { Directory.Delete(_dataDir, recursive: true); } catch (Exception) { }
        try { Directory.Delete(_projectDir, recursive: true); } catch (Exception) { }
    }

    private static int FreePort()
    {
        TcpListener l = new(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port == 7635 ? FreePort() : port;
    }

    /// <summary>Build A and build B, copied once per class from this build's CLI and server output, with
    /// <c>current</c> pointing at B.</summary>
    public sealed class TwoBuilds : IDisposable
    {
        private static readonly string Configuration = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd('/', '\\')).Parent!.Name;
        private static readonly string ServerDir = Path.Combine(RepoPaths.Root, "src", "Aitm.Server", "bin", Configuration, "net10.0");
        private static readonly string CliDir = Path.Combine(RepoPaths.Root, "src", "Aitm.Cli", "bin", Configuration, "net10.0");

        public string Root { get; } = Directory.CreateTempSubdirectory("aitm-builds-").FullName;
        public string StampA { get; } = new('a', 64);
        public string StampB { get; } = new('b', 64);
        public string Current => Path.Combine(Root, "current");

        public TwoBuilds()
        {
            foreach (string stamp in new[] { StampA, StampB })
            {
                string build = BuildDir(stamp);
                CopyTree(CliDir, Path.Combine(build, "bin-cli"));
                CopyTree(ServerDir, Path.Combine(build, "bin-server"));
                File.WriteAllText(Path.Combine(build, "bin-cli", "build-stamp.txt"), stamp);
            }
            PointCurrent(BuildDir(StampB));
        }

        public string BuildDir(string stamp) => Path.Combine(Root, "builds", stamp[..12]);

        private void PointCurrent(string target)
        {
            if (OperatingSystem.IsWindows())
            {
                // A junction, as build-cli-and-server.mjs makes it: no admin rights needed.
                using Process mklink = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{Current}\" \"{target}\"")
                    { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
                mklink.StandardOutput.ReadToEnd();
                mklink.WaitForExit();
                if (mklink.ExitCode != 0) throw new InvalidOperationException($"mklink /J failed with exit {mklink.ExitCode}");
            }
            else
            {
                Directory.CreateSymbolicLink(Current, target);
            }
        }

        private static void CopyTree(string from, string to)
        {
            Assert.True(Directory.Exists(from), $"not built: {from}");
            foreach (string dir in Directory.GetDirectories(from, "*", SearchOption.AllDirectories))
                Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
            Directory.CreateDirectory(to);
            foreach (string file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
                File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)));
        }

        public void Dispose()
        {
            try { Directory.Delete(Current); } catch (Exception) { }
            try { Directory.Delete(Root, recursive: true); } catch (Exception) { }
        }
    }
}
