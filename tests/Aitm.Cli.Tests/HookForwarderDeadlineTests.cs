using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Aitm.Cli.Tools;
using Xunit;

namespace Aitm.Cli.Tests;

/// <summary>
/// A forwarded hook (RESTRUCTURE.md slice 30) must end by its own deadline. An async command hook's timeout is
/// not enforced in an interactive session, so a server that accepts the connection and never answers would
/// otherwise keep the hook process alive for the rest of the session.
/// </summary>
public class HookForwarderDeadlineTests
{
    [Fact]
    public async Task AServerThatAcceptsAndNeverAnswersEndsTheHookAtItsDeadlineWithNoOutput()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Assert.NotEqual(7635, port);
        List<TcpClient> accepted = [];
        Task acceptLoop = Task.Run(() =>
        {
            try
            {
                while (true) accepted.Add(listener.AcceptTcpClient());
            }
            catch (SocketException) { /* listener stopped */ }
        });
        string dataDir = Directory.CreateTempSubdirectory("aitm-hook-deadline-").FullName;
        try
        {
            TimeSpan deadline = TimeSpan.FromSeconds(1);
            Stopwatch sw = Stopwatch.StartNew();

            Task<string> call = Task.Run(() => HookForwarder.Forward("SessionEnd", "{}", port, dataDir, null, deadline));
            bool finished = await Task.WhenAny(call, Task.Delay(TimeSpan.FromSeconds(10))) == call;
            long elapsedMs = sw.ElapsedMilliseconds;

            Assert.True(finished, "the forwarded hook was still waiting 10 s after a 1 s deadline");
            Assert.Equal("", await call);
            // The margin covers the first HTTP call's start-up on a cold CI runner (2.4 s seen on Linux), not the
            // deadline itself: a forwarder that ignores the deadline hangs until the 10 s guard above.
            Assert.True(elapsedMs < deadline.TotalMilliseconds + 5000, $"the forwarded hook took {elapsedMs} ms against a 1 s deadline");
        }
        finally
        {
            listener.Stop();
            await Task.WhenAny(acceptLoop, Task.Delay(TimeSpan.FromSeconds(5)));
            foreach (TcpClient client in accepted) client.Dispose();
            Directory.Delete(dataDir, recursive: true);
        }
    }
}
