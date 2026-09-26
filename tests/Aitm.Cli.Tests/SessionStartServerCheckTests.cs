using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Aitm.Cli.Tools;
using Xunit;

namespace Aitm.Cli.Tests;

/// <summary>
/// RESTRUCTURE.md slice 28: "A SessionStart check starts the server when /health does not answer."
/// Card: bounded wait, fail quietly (exit 0), behind `aitm hook SessionStart`.
/// </summary>
public class SessionStartServerCheckTests
{
    [Fact]
    public void ServerUpIsReusedAndNeverStarted()
    {
        int starts = 0;
        int exit = SessionStartServerCheck.Run(() => true, () => { starts++; return true; }, TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(10));

        Assert.Equal(0, exit);
        Assert.Equal(0, starts);
    }

    [Fact]
    public void ServerDownIsStartedOnceAndTheCheckWaitsUntilHealthAnswers()
    {
        int starts = 0;
        int probes = 0;
        bool Healthy() { probes++; return starts > 0 && probes >= 3; }

        int exit = SessionStartServerCheck.Run(Healthy, () => { starts++; return true; }, TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(10));

        Assert.Equal(0, exit);
        Assert.Equal(1, starts);
        Assert.True(probes >= 3);
    }

    [Fact]
    public void ServerThatNeverAnswersEndsWithinTheBoundAndExitsZero()
    {
        Stopwatch sw = Stopwatch.StartNew();
        int starts = 0;

        int exit = SessionStartServerCheck.Run(() => false, () => { starts++; return true; }, TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(20));

        Assert.Equal(0, exit);
        Assert.Equal(1, starts);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), $"took {sw.Elapsed}");
    }

    [Fact]
    public void AStartThatThrowsOrFailsIsQuiet()
    {
        Assert.Equal(0, SessionStartServerCheck.Run(() => false, () => throw new InvalidOperationException("boom"), TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(10)));
        Assert.Equal(0, SessionStartServerCheck.Run(() => throw new HttpRequestException("x"), () => false, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(10)));
    }

    [Fact]
    public void TheDefaultServerPathIsTheSiblingBinServerFolderOfTheCli()
    {
        string cliDir = Path.Combine("root", "bin-cli");
        string expectedName = OperatingSystem.IsWindows() ? "Aitm.Server.exe" : "Aitm.Server";

        Assert.Equal(Path.GetFullPath(Path.Combine("root", "bin-server", expectedName)), SessionStartServerCheck.DefaultServerPath(cliDir));
    }

    [Fact]
    public void HealthProbeAgainstAClosedPortIsFalse()
    {
        Assert.False(SessionStartServerCheck.IsHealthy(FreePort(), TimeSpan.FromMilliseconds(500)));
    }

    [Fact]
    public void TheBuiltHookWithNoServerAndNoServerExeExitsZeroFastAndPrintsNothing()
    {
        string dataDir = Directory.CreateTempSubdirectory("aitm-ss-").FullName;
        try
        {
            Stopwatch sw = Stopwatch.StartNew();
            (string stdout, _, int exit) = BuiltCli.Run(["hook", "SessionStart"], dataDir, new Dictionary<string, string>
            {
                ["AITM_SERVER_PORT"] = FreePort().ToString(),
                ["AITM_SERVER_EXE"] = Path.Combine(dataDir, "missing", "Aitm.Server.exe"),
            });

            Assert.Equal(0, exit);
            Assert.Equal("", stdout);
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(15), $"took {sw.Elapsed}");
        }
        finally
        {
            Directory.Delete(dataDir, recursive: true);
        }
    }

    private static int FreePort()
    {
        TcpListener l = new(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}
