using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Aitm.Layout.Tests;
using Xunit;
using Xunit.Abstractions;

namespace Aitm.Server.Tests;

// RESTRUCTURE.md slice 29d: the published Aitm.Cli (`aitm`) as a thin client of POST /cli. A real Aitm.Server
// process (this build's output) runs on an ephemeral port with a temp data dir; the CLI runs as its own
// process with that port and data dir. The oracle is the same call made straight to /cli. The cli-exit
// contract (cli-exit.test.mjs: unknown verb 2, flag before the verb 2, dropped verbs 2) holds through the
// thin client. And with no server running, a verb starts the configured server and still answers.
public sealed class ThinClientAgainstTheRunningServerTests : IDisposable
{
    private static readonly string Configuration = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd('/', '\\')).Parent!.Name;
    private static readonly string ServerDir = Path.Combine(RepoPaths.Root, "src", "Aitm.Server", "bin", Configuration, "net10.0");
    private static readonly string CliDll = Path.Combine(RepoPaths.Root, "src", "Aitm.Cli", "bin", Configuration, "net10.0", "aitm.dll");

    private readonly ITestOutputHelper _output;
    private readonly string _dataDir = Directory.CreateTempSubdirectory("aitm-thin-srv-").FullName;
    private readonly string _projectDir;
    private readonly int _port = FreePort();
    private readonly List<Process> _servers = [];

    public ThinClientAgainstTheRunningServerTests(ITestOutputHelper output)
    {
        _output = output;
        _projectDir = Path.Combine(Path.GetTempPath(), $"test-thin-srv-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_projectDir);
    }

    private void StartServer()
    {
        ProcessStartInfo psi = new("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add(Path.Combine(ServerDir, "Aitm.Server.dll"));
        psi.Environment["AITM_DATA_DIR"] = _dataDir;
        psi.Environment["AITM_SERVER_PORT"] = _port.ToString();
        psi.Environment.Remove("CLAUDE_PROJECT_DIR");
        psi.Environment.Remove("AITM_INSTANCE");
        Process process = Process.Start(psi)!;
        _servers.Add(process);
        Stopwatch sw = Stopwatch.StartNew();
        using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(1) };
        while (sw.Elapsed < TimeSpan.FromSeconds(20))
        {
            try
            {
                if (client.GetAsync($"http://127.0.0.1:{_port}/health").Result.IsSuccessStatusCode) return;
            }
            catch (Exception) when (!process.HasExited) { }
            Thread.Sleep(100);
        }
        throw new InvalidOperationException("the test server never answered /health");
    }

    private (int Exit, string Stdout, string Stderr) PostCli(string[] args)
    {
        using HttpClient client = new();
        using HttpRequestMessage request = new(HttpMethod.Post, $"http://127.0.0.1:{_port}/cli");
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {File.ReadAllText(Path.Combine(_dataDir, "server.token")).Trim()}");
        request.Headers.TryAddWithoutValidation("Claude-Project-Dir", _projectDir);
        request.Content = new StringContent(JsonSerializer.Serialize(new { args, cwd = _projectDir }), Encoding.UTF8, "application/json");
        using HttpResponseMessage response = client.Send(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument doc = JsonDocument.Parse(response.Content.ReadAsStringAsync().Result);
        return (doc.RootElement.GetProperty("exitCode").GetInt32(), doc.RootElement.GetProperty("stdout").GetString()!, doc.RootElement.GetProperty("stderr").GetString()!);
    }

    private (int Exit, string Stdout, string Stderr) RunThinCli(string[] args, string? serverExe = null)
    {
        Assert.True(File.Exists(CliDll), $"aitm not built at {CliDll}");
        ProcessStartInfo psi = new("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
        psi.ArgumentList.Add(CliDll);
        foreach (string a in args) psi.ArgumentList.Add(a);
        psi.Environment["AITM_DATA_DIR"] = _dataDir;
        psi.Environment["AITM_SERVER_PORT"] = _port.ToString();
        psi.Environment["AITM_SERVER_EXE"] = serverExe ?? Path.Combine(_dataDir, "missing", "Aitm.Server.exe");
        psi.Environment["CLAUDE_PROJECT_DIR"] = _projectDir;
        psi.Environment.Remove("AITM_INSTANCE");
        using Process p = Process.Start(psi)!;
        p.StandardInput.Close();
        Task<string> stdout = p.StandardOutput.ReadToEndAsync();
        Task<string> stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(60000))
        {
            p.Kill(entireProcessTree: true);
            throw new TimeoutException("aitm did not exit within 60 s");
        }
        return (p.ExitCode, stdout.Result, stderr.Result);
    }

    public static TheoryData<string, string[]> Verbs() => new()
    {
        { "help", ["help"] },
        { "todos", ["todos"] },
        { "unknown-verb", ["no-such-verb"] },
        { "dropped-verb", ["loop"] },
    };

    [Theory]
    [MemberData(nameof(Verbs))]
    public void AVerbThroughTheThinClientEqualsTheSameCallToCli(string name, string[] args)
    {
        StartServer();

        (int exit, string stdout, string stderr) direct = PostCli(args);
        (int exit, string stdout, string stderr) thin = RunThinCli(args);

        Assert.True(direct.exit == thin.exit, $"{name}: /cli {direct.exit}, thin client {thin.exit}; stderr {thin.stderr}");
        Assert.Equal(direct.stdout, thin.stdout);
        Assert.Equal(direct.stderr, thin.stderr);
    }

    [Fact]
    public void TheCliExitContractHoldsThroughTheThinClient()
    {
        StartServer();

        (int exit, string _, string stderr) unknown = RunThinCli(["no-such-command", "--instance", "test"]);
        (int exit, string _, string stderr) wrongOrder = RunThinCli(["--instance", "test", "selftest"]);
        (int exit, string stdout, string _) help = RunThinCli(["help", "--instance", "test"]);
        (int exit, string _, string stderr) loopStart = RunThinCli(["loop", "start", "3", "sometask", "--instance", "test"]);
        (int exit, string _, string stderr) selftest = RunThinCli(["selftest", "--instance", "test"]);

        Assert.Equal(2, unknown.exit);
        Assert.Contains("unknown command 'no-such-command'", unknown.stderr);
        Assert.Equal(2, wrongOrder.exit);
        Assert.Contains("after the command", wrongOrder.stderr);
        Assert.Equal(0, help.exit);
        Assert.Contains("aitm <command>", help.stdout);
        Assert.Equal(2, loopStart.exit);
        Assert.Contains("removed in 0.4", loopStart.stderr);
        Assert.Equal(2, selftest.exit);
        Assert.Contains("removed in 0.4", selftest.stderr);
    }

    [Fact]
    public void WithTheServerDownAVerbStartsTheConfiguredServerAndAnswers()
    {
        string exe = Path.Combine(ServerDir, OperatingSystem.IsWindows() ? "Aitm.Server.exe" : "Aitm.Server");
        Assert.True(File.Exists(exe), $"server not built at {exe}");
        DateTime before = DateTime.Now.AddSeconds(-1);
        Stopwatch sw = Stopwatch.StartNew();

        (int exit, string stdout, string stderr) = RunThinCli(["help"], exe);
        long startedMs = sw.ElapsedMilliseconds;
        sw.Restart();
        (int warmExit, _, _) = RunThinCli(["help"], exe);
        long warmMs = sw.ElapsedMilliseconds;
        _output.WriteLine($"thin client, server started on demand: {startedMs} ms; server already up: {warmMs} ms");

        try
        {
            Assert.True(exit == 0, $"exit {exit}; stderr {stderr}");
            Assert.Contains("aitm <command>", stdout);
            Assert.Equal(0, warmExit);
        }
        finally
        {
            foreach (Process p in Process.GetProcessesByName("Aitm.Server"))
            {
                try
                {
                    if (p.StartTime >= before && string.Equals(p.MainModule?.FileName, exe, StringComparison.OrdinalIgnoreCase))
                    {
                        p.Kill(entireProcessTree: true);
                        p.WaitForExit(5000);
                    }
                }
                catch (Exception) { /* not ours, or already gone */ }
                finally { p.Dispose(); }
            }
        }
    }

    public void Dispose()
    {
        foreach (Process p in _servers)
        {
            try { if (!p.HasExited) { p.Kill(entireProcessTree: true); p.WaitForExit(5000); } } catch (Exception) { }
            p.Dispose();
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
}
