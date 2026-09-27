using System.Diagnostics;
using Grimora.Layout.Tests;

namespace Grimora.Server.Tests;

/// <summary>
/// A real Grimora.Server process (never TestServer, which bypasses Kestrel and the pipe) on a temp data
/// directory, and so on a pipe/socket name of its own. Disposing kills only the process started here.
/// </summary>
internal sealed class RunningServer : IDisposable
{
    private static readonly string Configuration = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd('/', '\\')).Parent!.Name;
    internal static readonly string ServerDir = Path.Combine(RepoPaths.Root, "src", "Grimora.Server", "bin", Configuration, "net10.0");
    internal static readonly string CliDll = Path.Combine(RepoPaths.Root, "src", "Grimora.Cli", "bin", Configuration, "net10.0", "grimora.dll");

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
        psi.ArgumentList.Add(Path.Combine(ServerDir, "Grimora.Server.dll"));
        psi.Environment["GRIMORA_DATA_DIR"] = dataDir;
        psi.Environment.Remove("CLAUDE_PROJECT_DIR");
        psi.Environment.Remove("GRIMORA_INSTANCE");
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

    /// <summary>Creates the project's store (schema included) under <paramref name="dataDir"/> and runs <paramref name="seed"/> on it.</summary>
    public static void SeedInstance(string dataDir, string instance, Action<Microsoft.Data.Sqlite.SqliteConnection> seed)
    {
        using (Grimora.Server.Data.ProjectStore bootstrap = new(dataDir)) bootstrap.Acquire(instance);
        HttpSnapshotParityTests.Seed(Path.Combine(dataDir, instance, "grimora.db"), seed);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    public HttpClient CreateClient() => PipeTestClient.CreateClient(DataDir, TimeSpan.FromSeconds(30));

    public void Dispose()
    {
        try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); } catch { /* already gone */ }
        _process.Dispose();
    }
}
