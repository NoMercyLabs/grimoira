using System.IO.Pipes;
using System.Net.Sockets;
using Grimoira.Server.Data;
using Xunit;

namespace Grimoira.Server.Tests;

// The window behind CallAtIdleExpiryTests' flake, made deterministic: a client whose connection the service has
// accepted, but whose request it has not read yet, when the service stops (idle exit and /shutdown both end in
// StopApplication). No sleep decides the order: the connection is open before the stop is asked for.
public sealed class ConnectionAcceptedAtStopTests : IDisposable
{
    private readonly string _dataDir = Directory.CreateTempSubdirectory("grimoira-accepted-at-stop-").FullName;

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

    // On Windows, Kestrel may close a connected pipe before reading a request during shutdown. If that happens,
    // the service leaves proof that it never ran: a clean-exit record covering the moment the call was sent,
    // written only when every call that did start was answered. The client resends on that proof alone.
    //
    // Windows-only contract (grimoira/issues/4): Linux CI served this request during graceful shutdown,
    // so the call was not lost and this assertion's premise did not hold. macOS has not been measured.
    [Fact]
    public async Task AnAcceptedCallAtStopIsAnsweredOrCoveredByTheCleanExitRecord()
    {
        // xUnit 2.9.2's [Fact] Skip needs a compile-time constant, so this Windows-only assertion uses
        // a runtime platform check.
        if (!OperatingSystem.IsWindows()) return;

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
            HttpResponseMessage? response = null;
            Exception? failure = await Record.ExceptionAsync(async () => { response = await late.PostAsync("http://grimoira-pipe.local/tools/recall", null); });
            // Windows reports the dead pipe as HttpRequestException; on Linux, the Unix domain socket the
            // peer already closed surfaces as ObjectDisposedException from inside SocketsHttpHandler's own
            // connection reuse instead. Both mean the same thing here: the call never got a response, which
            // is what CleanExitRecord.Judge below actually proves.
            Assert.True(failure is null or HttpRequestException or ObjectDisposedException,
                $"unexpected call failure: {failure?.GetType().FullName}");
            if (failure is null) Assert.NotNull(response);
            response?.Dispose();

            // The service is gone once its single-instance lock is free.
            using (FileStream? held = WaitForFreeLock()) Assert.NotNull(held);
            string rec = File.Exists(Path.Combine(_dataDir, CleanExitRecord.FileName)) ? File.ReadAllText(Path.Combine(_dataDir, CleanExitRecord.FileName)) : "none";
            if (failure is not null)
                Assert.True(CleanExitRecord.Judge(CleanExitRecord.Read(_dataDir), sentAt, "an-id-the-service-never-read") == LostCallVerdict.NeverRan,
                    $"the service exited without a clean-exit record proving the dropped call never ran (record: {rec}, sent {sentAt.Ticks})");
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

    [Fact]
    public void ARecordFromAnEarlierRunIsRemovedWhenTheServiceStarts()
    {
        DateTime earlier = DateTime.UtcNow.AddMinutes(-5);
        new CleanExitRecord(earlier, earlier.AddMinutes(1), Clean: true, Wrapped: false, earlier, []).Write(_dataDir);

        using RunningServer server = RunningServer.Start(_dataDir);

        Assert.False(File.Exists(Path.Combine(_dataDir, CleanExitRecord.FileName)), "the running service kept an earlier run's exit record");
    }
}
