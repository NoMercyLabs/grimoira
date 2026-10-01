using System.Diagnostics;
using Grimoira.Layout.Tests;

namespace Grimoira.Server.Tests;

/// <summary>
/// A real Grimoira.Server process (never TestServer, which bypasses Kestrel and the pipe) on a temp data
/// directory, and so on a pipe/socket name of its own. Disposing kills only the process started here.
/// </summary>
internal sealed class RunningServer : IDisposable
{
    private static readonly string Configuration = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd('/', '\\')).Parent!.Name;
    internal static readonly string ServerDir = Path.Combine(RepoPaths.Root, "src", "Grimoira.Server", "bin", Configuration, "net10.0");
    internal static readonly string CliDll = Path.Combine(RepoPaths.Root, "src", "Grimoira.Cli", "bin", Configuration, "net10.0", "grimoira.dll");

    private readonly Process _process;

    public string DataDir { get; }

    private RunningServer(string dataDir, Process process)
    {
        DataDir = dataDir;
        _process = process;
    }

    public static RunningServer Start(string dataDir, IReadOnlyDictionary<string, string>? environment = null)
    {
        ProcessStartInfo psi = new("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add(Path.Combine(ServerDir, "Grimoira.Server.dll"));
        psi.Environment["GRIMOIRA_DATA_DIR"] = dataDir;
        psi.Environment.Remove("CLAUDE_PROJECT_DIR");
        psi.Environment.Remove("GRIMOIRA_INSTANCE");
        foreach ((string key, string value) in environment ?? new Dictionary<string, string>()) psi.Environment[key] = value;
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
        using (Grimoira.Server.Data.ProjectStore bootstrap = new(dataDir)) bootstrap.Acquire(instance);
        HttpSnapshotParityTests.Seed(Path.Combine(dataDir, instance, "grimoira.db"), seed);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    public HttpClient CreateClient() => PipeTestClient.CreateClient(DataDir, TimeSpan.FromSeconds(30));

    public void Dispose()
    {
        try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); } catch { /* already gone */ }
        _process.Dispose();
    }
}
