using System.IO.Pipes;
using System.Net.Sockets;

namespace Aitm.Cli.Tools;

/// <summary>
/// RESTRUCTURE.md Slice P1: the one shared connection helper every client site (ThinClient, HookForwarder,
/// ServerAutoStart, ServerHandover) uses to reach Aitm.Server over its local pipe / Unix domain socket
/// instead of <c>127.0.0.1:7635</c>. <see cref="CreateClient"/> hands back a plain <see cref="HttpClient"/>
/// with a <see cref="SocketsHttpHandler.ConnectCallback"/> that opens the transport for the data
/// directory's derived address (<see cref="ServerAddress"/>); every route stays exactly as it is (relative
/// paths against <see cref="BaseAddress"/>), only the transport underneath changes.
/// </summary>
public static class PipeConnection
{
    /// <summary>A placeholder host: the ConnectCallback below ignores it entirely and opens the pipe or
    /// socket instead, but HttpClient needs a well-formed base address to resolve relative request URIs.</summary>
    public static readonly Uri BaseAddress = new("http://aitm-pipe.local/");

    /// <summary>An HttpClient that reaches the service for <paramref name="dataDir"/> over a named pipe
    /// (Windows) or a Unix domain socket (macOS/Linux), never a TCP port. <paramref name="timeout"/> bounds
    /// both the connect and the whole call.</summary>
    public static HttpClient CreateClient(string dataDir, TimeSpan timeout) => CreateClient(dataDir, timeout, timeout);

    /// <summary>As above, with the connect wait and the whole-call budget bounded separately: a
    /// <see cref="NamedPipeClientStream"/> connect keeps retrying CreateFile until its own timeout elapses
    /// even when no pipe by that name exists at all (there is no fast "connection refused" the way a closed
    /// TCP port gives one), so a caller with a long overall budget (ThinClient's 960 s verb timeout) still
    /// needs a short <paramref name="connectTimeout"/> to fail fast against a server that is simply down.</summary>
    public static HttpClient CreateClient(string dataDir, TimeSpan connectTimeout, TimeSpan overallTimeout)
    {
        SocketsHttpHandler handler = new()
        {
            ConnectCallback = async (_, cancellationToken) => await ConnectAsync(dataDir, connectTimeout, cancellationToken),
            ConnectTimeout = connectTimeout,
        };
        return new HttpClient(handler) { BaseAddress = BaseAddress, Timeout = overallTimeout };
    }

    private static async ValueTask<System.IO.Stream> ConnectAsync(string dataDir, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
        {
            NamedPipeClientStream pipe = new(".", ServerAddress.PipeName(dataDir), PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await pipe.ConnectAsync((int)timeout.TotalMilliseconds, cancellationToken);
                return pipe;
            }
            catch
            {
                pipe.Dispose();
                throw;
            }
        }

        Socket socket = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(ServerAddress.SocketPath(dataDir)), cancellationToken);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
