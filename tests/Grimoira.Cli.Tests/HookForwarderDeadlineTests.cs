using System.Diagnostics;
using System.IO.Pipes;
using System.Net.Sockets;
using Grimoira.Cli.Tools;
using Xunit;

namespace Grimoira.Cli.Tests;

/// <summary>
/// A forwarded hook (RESTRUCTURE.md slice 30) must end by its own deadline. An async command hook's timeout is
/// not enforced in an interactive session, so a server that accepts the connection and never answers would
/// otherwise keep the hook process alive for the rest of the session. Slice P1: the transport is the local
/// pipe / Unix socket, so the stand-in "server" here accepts on the same transport and simply never answers.
/// </summary>
public class HookForwarderDeadlineTests
{
    [Fact]
    public async Task AServerThatAcceptsAndNeverAnswersEndsTheHookAtItsDeadlineWithNoOutput()
    {
        string dataDir = Directory.CreateTempSubdirectory("grimoira-hook-deadline-").FullName;
        using CancellationTokenSource acceptCancel = new();
        Task acceptLoop = OperatingSystem.IsWindows()
            ? AcceptForeverOnNamedPipeAsync(dataDir, acceptCancel.Token)
            : AcceptForeverOnUnixSocketAsync(dataDir, acceptCancel.Token);
        try
        {
            TimeSpan deadline = TimeSpan.FromSeconds(1);
            Stopwatch sw = Stopwatch.StartNew();

            Task<string> call = Task.Run(() => HookForwarder.Forward("SessionEnd", "{}", dataDir, null, deadline));
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
            acceptCancel.Cancel();
            await Task.WhenAny(acceptLoop, Task.Delay(TimeSpan.FromSeconds(5)));
            Directory.Delete(dataDir, recursive: true);
        }
    }

    [Fact]
    public void AHookWhoseServiceIsGoneButStillHoldsItsLockEndsAtItsDeadline()
    {
        string dataDir = Directory.CreateTempSubdirectory("grimoira-hook-stalled-").FullName;
        // The old service: nothing listens any more, but server.lock stays held; the start of a new one stalls.
        FileStream held = new(Path.Combine(dataDir, "server.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            TimeSpan deadline = TimeSpan.FromSeconds(1);
            Stopwatch sw = Stopwatch.StartNew();

            string output = HookForwarder.Forward("SessionEnd", "{}", dataDir, null, deadline, () => { Thread.Sleep(TimeSpan.FromSeconds(15)); return false; });

            Assert.Equal("", output);
            Assert.True(sw.Elapsed < deadline + TimeSpan.FromSeconds(2), $"the hook took {sw.ElapsedMilliseconds} ms against a 1 s deadline");
        }
        finally
        {
            held.Dispose();
            Directory.Delete(dataDir, recursive: true);
        }
    }

    private static async Task AcceptForeverOnNamedPipeAsync(string dataDir, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                using NamedPipeServerStream pipe = new(ServerAddress.PipeName(dataDir), PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await pipe.WaitForConnectionAsync(cancellationToken);
                await Task.Delay(Timeout.Infinite, cancellationToken); // accepted; never answers, held until cancelled
            }
        }
        catch (OperationCanceledException) { /* the test is tearing down */ }
    }

    private static async Task AcceptForeverOnUnixSocketAsync(string dataDir, CancellationToken cancellationToken)
    {
        string path = ServerAddress.SocketPath(dataDir);
        try { File.Delete(path); } catch (IOException) { /* nothing to remove */ }
        using Socket listener = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(path));
        listener.Listen(1);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                using Socket accepted = await listener.AcceptAsync(cancellationToken);
                await Task.Delay(Timeout.Infinite, cancellationToken); // accepted; never answers, held until cancelled
            }
        }
        catch (OperationCanceledException) { /* the test is tearing down */ }
    }
}
