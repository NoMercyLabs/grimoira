using System.Diagnostics;
using Aitm.Layout.Tests;

namespace Aitm.Server.Tests;

/// <summary>
/// A real Aitm.Server process (never TestServer, which bypasses Kestrel and the pipe) on a temp data
/// directory, and so on a pipe/socket name of its own. Disposing kills only the process started here.
/// </summary>
internal sealed class RunningServer : IDisposable
{
    private static readonly string Configuration = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd('/', '\\')).Parent!.Name;
    internal static readonly string ServerDir = Path.Combine(RepoPaths.Root, "src", "Aitm.Server", "bin", Configuration, "net10.0");
    internal static readonly string CliDll = Path.Combine(RepoPaths.Root, "src", "Aitm.Cli", "bin", Configuration, "net10.0", "aitm.dll");

    private readonly Process _process;

    public string DataDir { get; }

    private RunningServer(string dataDir, Process process)
    {
        DataDir = dataDir;
        _process = process;
    }

    public static RunningServer Start(string dataDir)
    {
        ProcessStartInfo psi = new("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add(Path.Combine(ServerDir, "Aitm.Server.dll"));
        psi.Environment["AITM_DATA_DIR"] = dataDir;
        psi.Environment.Remove("CLAUDE_PROJECT_DIR");
        psi.Environment.Remove("AITM_INSTANCE");
        Process process = Process.Start(psi)!;
        _ = process.StandardOutput.ReadToEndAsync();
        _ = process.StandardError.ReadToEndAsync();
        RunningServer server = new(dataDir, process);
        Stopwatch sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(20))
        {
            try
            {
                using HttpClient client = PipeTestClient.CreateClient(dataDir, TimeSpan.FromSeconds(1));
                if (client.GetAsync("/health").Result.IsSuccessStatusCode) return server;
            }
            catch (Exception) when (!process.HasExited) { }
            Thread.Sleep(100);
        }
        server.Dispose();
        throw new InvalidOperationException("the test server never answered /health over the pipe");
    }

    public HttpClient CreateClient() => PipeTestClient.CreateClient(DataDir, TimeSpan.FromSeconds(30));

    public void Dispose()
    {
        try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); } catch { /* already gone */ }
        _process.Dispose();
    }
}
