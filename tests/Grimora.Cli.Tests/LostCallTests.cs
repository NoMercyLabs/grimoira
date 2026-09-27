using System.IO.Pipes;
using System.Net.Sockets;
using System.Text;
using Grimora.Cli.Tools;
using Grimora.Server.Data;
using Xunit;

namespace Grimora.Cli.Tests;

// A call whose connection fails with no answer is resent only with proof that it never ran: the exiting service's
// clean-exit record covers the moment it was sent and does not list the call's id. A stand-in service on the real
// transport (pipe / Unix socket) plays the old service; the client under test is ThinClient.
public sealed class LostCallTests : IDisposable
{
    private readonly string _dataDir = Directory.CreateTempSubdirectory("grimora-lost-call-").FullName;
    private int _runs;
    private readonly List<string?> _ids = [];

    public void Dispose()
    {
        try { Directory.Delete(_dataDir, recursive: true); } catch (IOException) { /* a stand-in may still be closing */ }
    }

    private enum Mode { RunThenReset, NeverRead, Answer }

    /// <summary>One service life: holds server.lock, takes one connection in <paramref name="mode"/>, then exits
    /// cleanly (writes its record, frees the lock).</summary>
    private Task ServeOnce(Mode mode, TaskCompletionSource? listening = null) => Task.Run(async () =>
    {
        DateTime started = DateTime.UtcNow;
        FileStream held = new(Path.Combine(_dataDir, "server.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        List<string> startedIds = [];
        try
        {
            await using Stream connection = await AcceptOne(listening);
            if (mode == Mode.NeverRead) return;
            (string? id, _) = await ReadRequest(connection);
            lock (_ids) { _ids.Add(id); _runs++; }
            if (id is not null) startedIds.Add(id);
            if (mode == Mode.Answer)
            {
                byte[] body = Encoding.UTF8.GetBytes("{\"stdout\":\"ran\",\"stderr\":\"\",\"exitCode\":0}");
                byte[] head = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
                await connection.WriteAsync(head);
                await connection.WriteAsync(body);
                await connection.FlushAsync();
            }
            // RunThenReset: the call ran; the connection closes with no answer.
        }
        finally
        {
            if (mode != Mode.Answer) WriteRecord(started, DateTime.UtcNow, startedIds);
            held.Dispose();
        }
    });

    private void WriteRecord(DateTime started, DateTime exited, List<string> ids) =>
        new CleanExitRecord(started, exited, Clean: true, Wrapped: false, OldestKeptStartUtc: started, ids).Write(_dataDir);

    private async Task<Stream> AcceptOne(TaskCompletionSource? listening)
    {
        if (OperatingSystem.IsWindows())
        {
            NamedPipeServerStream pipe = new(ServerAddress.PipeName(_dataDir), PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            listening?.SetResult();
            await pipe.WaitForConnectionAsync();
            return pipe;
        }
        string path = ServerAddress.SocketPath(_dataDir);
        try { File.Delete(path); } catch (IOException) { /* nothing to remove */ }
        using Socket listener = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(path));
        listener.Listen(1);
        listening?.SetResult();
        Socket accepted = await listener.AcceptAsync();
        File.Delete(path);
        return new NetworkStream(accepted, ownsSocket: true);
    }

    private static async Task<(string? Id, string Head)> ReadRequest(Stream connection)
    {
        List<byte> bytes = [];
        byte[] one = new byte[1];
        while (!(bytes.Count >= 4 && bytes[^4] == '\r' && bytes[^3] == '\n' && bytes[^2] == '\r' && bytes[^1] == '\n'))
        {
            if (await connection.ReadAsync(one) == 0) break;
            bytes.Add(one[0]);
        }
        string head = Encoding.ASCII.GetString([.. bytes]);
        string? id = null;
        int length = 0;
        foreach (string line in head.Split("\r\n"))
        {
            if (line.StartsWith("X-Grimora-Call:", StringComparison.OrdinalIgnoreCase)) id = line[15..].Trim();
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) length = int.Parse(line[15..].Trim(), System.Globalization.CultureInfo.InvariantCulture);
        }
        byte[] body = new byte[length];
        int read = 0;
        while (read < length) { int n = await connection.ReadAsync(body.AsMemory(read)); if (n == 0) break; read += n; }
        return (id, head);
    }

    private (int Exit, string Stdout, string Stderr) Call(Func<bool> ensureServer)
    {
        StringWriter stdout = new();
        StringWriter stderr = new();
        int exit = ThinClient.Run(["help"], _dataDir, _dataDir, null, _dataDir, ensureServer, Path.Combine(_dataDir, "none.exe"), stdout, stderr);
        return (exit, stdout.ToString(), stderr.ToString());
    }

    private Func<bool> StartsAnAnsweringService(List<Task> started) => () =>
    {
        TaskCompletionSource listening = new();
        started.Add(ServeOnce(Mode.Answer, listening));
        listening.Task.Wait();
        return true;
    };

    [Fact]
    public async Task ACallTheServiceRanBeforeItsAnswerWasLostIsNotRepeated()
    {
        TaskCompletionSource listening = new();
        Task old = ServeOnce(Mode.RunThenReset, listening);
        await listening.Task;
        List<Task> started = [];

        (int exit, _, string stderr) = Call(StartsAnAnsweringService(started));
        await old;

        Assert.Equal(1, _runs);
        Assert.Equal(1, exit);
        Assert.True(stderr.Contains("it may have run; not repeated", StringComparison.Ordinal), stderr);
    }

    [Fact]
    public async Task ACallTheServiceNeverReadIsResentOnceAndRunsOnce()
    {
        TaskCompletionSource listening = new();
        Task old = ServeOnce(Mode.NeverRead, listening);
        await listening.Task;
        List<Task> started = [];

        (int exit, string stdout, string stderr) = Call(StartsAnAnsweringService(started));
        await old;
        await Task.WhenAll(started);

        Assert.True(exit == 0, $"the call was not answered: {stderr}");
        Assert.Equal("ran", stdout);
        Assert.Equal(1, _runs);
        _ = Assert.Single(started);
        Assert.NotNull(_ids[0]);
    }
}
