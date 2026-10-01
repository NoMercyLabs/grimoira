using System.Diagnostics;
using Xunit;

namespace Grimoira.Server.Tests;

// A client that connects while the service is exiting on idle must end with its call answered, silently, by a
// new service. Real processes on a temp data dir (its own pipe): the CLI starts the service on demand with a
// 2 s idle time, and calls are issued around the moment of expiry.
public sealed class CallAtIdleExpiryTests : IDisposable
{
    private readonly string _dataDir = Directory.CreateTempSubdirectory("grimoira-idle-race-").FullName;

    public void Dispose()
    {
        Cli("service", "stop");
        Thread.Sleep(500);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataDir, recursive: true); } catch { /* the service may still be closing its store */ }
    }

    private (int Exit, string Stdout, string Stderr) Cli(params string[] args)
    {
        ProcessStartInfo psi = new("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
        psi.ArgumentList.Add(RunningServer.CliDll);
        foreach (string a in args) psi.ArgumentList.Add(a);
        psi.Environment["GRIMOIRA_DATA_DIR"] = _dataDir;
        psi.Environment["GRIMOIRA_IDLE_SECONDS"] = "2";
        psi.Environment["GRIMOIRA_SERVER_EXE"] = Path.Combine(RunningServer.ServerDir, OperatingSystem.IsWindows() ? "Grimoira.Server.exe" : "Grimoira.Server");
        psi.Environment.Remove("GRIMOIRA_INSTANCE");
        psi.Environment["CLAUDE_PROJECT_DIR"] = Path.Combine(_dataDir, "project");
        using Process p = Process.Start(psi)!;
        p.StandardInput.Close();
        Task<string> stdout = p.StandardOutput.ReadToEndAsync();
        Task<string> stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(60000)) { p.Kill(entireProcessTree: true); throw new TimeoutException("grimoira did not exit within 60 s"); }
        return (p.ExitCode, stdout.Result, stderr.Result);
    }

    [Fact]
    public void CallsIssuedAroundTheIdleExpiryAreAllAnswered()
    {
        int[] gapsMs = [1700, 1900, 2000, 2100, 2300, 2600, 1800, 2000];
        (int exit, _, string error) = Cli("help");
        Assert.True(exit == 0, $"the first call failed: {error}");

        foreach (int gap in gapsMs)
        {
            Thread.Sleep(gap);
            (int callExit, string callOut, string callError) = Cli("help");

            Assert.True(callExit == 0, $"a call after {gap} ms was not answered (exit {callExit}): {callError}");
            Assert.NotEmpty(callOut);
        }

        // The service may already have exited on idle by now; one more call must still be answered (by it or a new one).
        (int lastExit, string lastOut, string lastError) = Cli("help");
        Assert.True(lastExit == 0, $"the closing call was not answered (exit {lastExit}): {lastError}");
        Assert.NotEmpty(lastOut);
    }
}
