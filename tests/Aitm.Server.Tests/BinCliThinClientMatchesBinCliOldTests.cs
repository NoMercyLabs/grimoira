using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Aitm.Layout.Tests;
using Aitm.TestSupport;
using Xunit;
using Xunit.Abstractions;

namespace Aitm.Server.Tests;

// RESTRUCTURE.md sub-card 29e: bin-cli/aitm.dll is now the published thin client and bin-cli-old/aitm.dll the
// last aitm.cs build. Every golden CLI verb's parity scenarios (the normal and error calls the slice 24
// *CliParityTests already list) run through both: the old build on its own ~/.aitm test-* instance, the thin
// client against a real Aitm.Server on an ephemeral port with a temp data dir. Stdout, stderr and exit code
// must match once the instance name, its store folder and timings are masked. AITM_SERVER_EXE points at a
// missing file, so a dead test server fails the test instead of starting the live one.
public sealed class BinCliThinClientMatchesBinCliOldTests : IClassFixture<BinCliThinClientMatchesBinCliOldTests.TestServer>
{
    private static readonly string NewDll = Path.Combine(RepoPaths.Root, "bin-cli", "aitm.dll");
    private static readonly string OldDll = Path.Combine(RepoPaths.Root, "bin-cli-old", "aitm.dll");

    private readonly TestServer _server;
    private readonly ITestOutputHelper _output;

    public BinCliThinClientMatchesBinCliOldTests(TestServer server, ITestOutputHelper output)
    {
        _server = server;
        _output = output;
    }

    public static IEnumerable<object[]> Scenarios() =>
        StoreFactsCliParityTests.Scenarios()
            .Concat(MemoryDocsCliParityTests.Scenarios())
            .Concat(GraphCliParityTests.Scenarios())
            .Concat(BrainReadCliParityTests.Scenarios())
            .Concat(BrainWriteCliParityTests.Scenarios())
            .Concat(StagingSpineEvalCliParityTests.Scenarios());

    [Theory]
    [MemberData(nameof(Scenarios))]
    public void TheThinClientAnswersLikeTheOldBuild(string name, string[] setup, string command)
    {
        string oldInstance = AitmCliRunner.NewTestInstance("thin-old");
        string newInstance = AitmCliRunner.NewTestInstance("thin-new");
        string oldRoot = AitmCliRunner.InstanceDir(oldInstance);
        string newRoot = Path.Combine(_server.DataDir, newInstance);
        try
        {
            foreach (string s in setup)
            {
                Run(OldDll, oldInstance, s);
                Run(NewDll, newInstance, s);
            }
            OldVsNewCli.Result o = Run(OldDll, oldInstance, command);
            OldVsNewCli.Result n = Run(NewDll, newInstance, command);

            Assert.Equal(Normalize(o.Stdout, oldInstance, oldRoot), Normalize(n.Stdout, newInstance, newRoot));
            Assert.Equal(Normalize(o.Stderr, oldInstance, oldRoot), Normalize(n.Stderr, newInstance, newRoot));
            Assert.True(o.ExitCode == n.ExitCode, $"{name}: old exit {o.ExitCode}, thin client exit {n.ExitCode}; stderr {n.Stderr}");
        }
        finally
        {
            AitmCliRunner.DeleteInstance(oldInstance);
        }
    }

    [Fact]
    public void TheScenariosCoverEveryGoldenCliVerb()
    {
        HashSet<string> covered = new(StringComparer.Ordinal);
        foreach (object[] s in Scenarios())
        {
            string[] words = ((string)s[2]).Split(' ');
            covered.Add(words[0]);
            // `brain <sub>` scenarios count for the brain sub-verbs the golden list names on their own.
            if (words[0] == "brain" && words.Length > 1) covered.Add(words[1]);
        }
        string[] missing = [.. GoldenListsTests.GoldenCliVerbs.Where(v => !covered.Contains(v))];
        _output.WriteLine($"golden verbs covered through the thin client: {GoldenListsTests.GoldenCliVerbs.Length - missing.Length} of {GoldenListsTests.GoldenCliVerbs.Length}; missing: {string.Join(", ", missing)}");
        Assert.True(missing.Length == 0, "not covered: " + string.Join(", ", missing));
    }

    // The instance name and its store folder differ per run by design; so do elapsed times (query) and the
    // mutation timestamps history prints (the same masks the slice 24 parity tests use).
    private static string Normalize(string s, string instance, string root) =>
        Regex.Replace(
            Regex.Replace(
                s.Replace(root, "<root>").Replace(root.Replace('\\', '/'), "<root>").Replace(instance, "<instance>"),
                @"\d+([.,]\d+)?\s?ms\b", "<ms>"),
            @"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d+Z", "<ts>");

    private OldVsNewCli.Result Run(string dll, string instance, string arguments)
    {
        ProcessStartInfo psi = new("dotnet", $"\"{dll}\" {arguments} --instance {instance}")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        psi.Environment["AITM_DATA_DIR"] = _server.DataDir;
        psi.Environment["AITM_SERVER_PORT"] = _server.Port.ToString();
        psi.Environment["AITM_SERVER_EXE"] = Path.Combine(_server.DataDir, "missing", "Aitm.Server.exe");
        psi.Environment.Remove("AITM_INSTANCE");
        psi.Environment.Remove("CLAUDE_PROJECT_DIR");
        using Process p = Process.Start(psi) ?? throw new InvalidOperationException($"could not start {dll}");
        p.StandardInput.Close();
        Task<string> stdout = p.StandardOutput.ReadToEndAsync();
        Task<string> stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(120000))
        {
            p.Kill(entireProcessTree: true);
            throw new TimeoutException($"{dll} {arguments} did not exit within 120 s");
        }
        return new OldVsNewCli.Result(stdout.Result, stderr.Result, p.ExitCode);
    }

    /// <summary>One real Aitm.Server (this build's output) per test class, on an ephemeral port that is
    /// never 7635, with its own temp data dir.</summary>
    public sealed class TestServer : IDisposable
    {
        private static readonly string Configuration = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd('/', '\\')).Parent!.Name;
        private static readonly string ServerDll = Path.Combine(RepoPaths.Root, "src", "Aitm.Server", "bin", Configuration, "net10.0", "Aitm.Server.dll");

        private readonly Process _process;
        public string DataDir { get; } = Directory.CreateTempSubdirectory("aitm-thin-parity-").FullName;
        public int Port { get; } = FreePort();

        public TestServer()
        {
            ProcessStartInfo psi = new("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            psi.ArgumentList.Add(ServerDll);
            psi.Environment["AITM_DATA_DIR"] = DataDir;
            psi.Environment["AITM_SERVER_PORT"] = Port.ToString();
            psi.Environment.Remove("CLAUDE_PROJECT_DIR");
            psi.Environment.Remove("AITM_INSTANCE");
            _process = Process.Start(psi)!;
            _process.OutputDataReceived += (_, _) => { };
            _process.ErrorDataReceived += (_, _) => { };
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
            Stopwatch sw = Stopwatch.StartNew();
            using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(1) };
            while (sw.Elapsed < TimeSpan.FromSeconds(30))
            {
                try
                {
                    if (client.GetAsync($"http://127.0.0.1:{Port}/health").Result.IsSuccessStatusCode) return;
                }
                catch (Exception) when (!_process.HasExited) { }
                Thread.Sleep(100);
            }
            Dispose();
            throw new InvalidOperationException("the test server never answered /health");
        }

        public void Dispose()
        {
            try { if (!_process.HasExited) { _process.Kill(entireProcessTree: true); _process.WaitForExit(5000); } } catch (Exception) { }
            _process.Dispose();
            try { Directory.Delete(DataDir, recursive: true); } catch (Exception) { }
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
}
