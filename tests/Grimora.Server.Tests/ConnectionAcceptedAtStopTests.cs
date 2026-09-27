using System.IO.Pipes;
using System.Net.Sockets;
using Grimora.Server.Data;
using Xunit;

namespace Grimora.Server.Tests;

// The window behind CallAtIdleExpiryTests' flake, made deterministic: a client whose connection the service has
// accepted, but whose request it has not read yet, when the service stops (idle exit and /shutdown both end in
// StopApplication). No sleep decides the order: the connection is open before the stop is asked for.
public sealed class ConnectionAcceptedAtStopTests : IDisposable
{
    private readonly string _dataDir = Directory.CreateTempSubdirectory("grimora-accepted-at-stop-").FullName;

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataDir, recursive: true); } catch { /* the service may still be closing its store */ }
    }

    private static async Task<Stream> OpenRaw(string dataDir)
    {
        if (OperatingSystem.IsWindows())
        {
            NamedPipeClientStream pipe = new(".", ServerAddress.PipeName(dataDir), PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(5000);
            return pipe;
        }
        Socket socket = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(ServerAddress.SocketPath(dataDir)));
        return new NetworkStream(socket, ownsSocket: true);
    }

    private static async Task<bool> Answers(string dataDir)
    {
        try
        {
            using HttpClient probe = PipeTestClient.CreateClient(dataDir, TimeSpan.FromMilliseconds(300));
            return (await probe.GetAsync("/health")).IsSuccessStatusCode;
        }
        catch (Exception) { return false; }
    }

    // Kestrel closes a connection that has not started a request when the service stops; on Windows the pipe
    // instance was connected before Kestrel ever read from it. That call is lost however the stop is ordered, so
    // the service leaves proof that it never ran: a clean-exit record covering the moment the call was sent,
    // written only when every call that did start was answered. The client resends on that proof alone.
    [Fact]
    public async Task ACallDroppedByTheStopIsCoveredByTheCleanExitRecord()
    {
        using RunningServer server = RunningServer.Start(_dataDir);
        // A client takes the time before it connects: that is when its call went to this service.
        DateTime sentAt = DateTime.UtcNow;
        Stream early = await OpenRaw(_dataDir);
        await using (early)
        {
            using (HttpClient client = server.CreateClient())
                Assert.True((await client.PostAsync("/shutdown", null)).IsSuccessStatusCode);
            // The listener is gone once a new connection no longer reaches the service: the stop is under way.
            while (await Answers(_dataDir)) await Task.Delay(20);

            SocketsHttpHandler handler = new() { ConnectCallback = (_, _) => ValueTask.FromResult(early) };
            using HttpClient late = new(handler);
            late.Timeout = TimeSpan.FromSeconds(20);
            Exception? failure = await Record.ExceptionAsync(() => late.PostAsync("http://grimora-pipe.local/tools/recall", null));
            Assert.IsType<HttpRequestException>(failure);

            // The service is gone once its single-instance lock is free.
            using (FileStream? held = WaitForFreeLock()) Assert.NotNull(held);
            string rec = File.Exists(Path.Combine(_dataDir, CleanExitRecord.FileName)) ? File.ReadAllText(Path.Combine(_dataDir, CleanExitRecord.FileName)) : "none";
            Assert.True(CleanExitRecord.Covers(_dataDir, sentAt), $"the service exited without a clean-exit record covering the dropped call (record: {rec}, sent {sentAt.Ticks})");
        }
    }

    private FileStream? WaitForFreeLock()
    {
        System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(20))
        {
            FileStream? held = ProcessOwner.TryAcquireSingleInstanceLock(_dataDir);
            if (held is not null) return held;
            Thread.Sleep(50);
        }
        return null;
    }
}
