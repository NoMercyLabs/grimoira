using System.IO.Pipes;
using System.Net.Sockets;
using Grimora.Server.Data;

namespace Grimora.Server.Tests;

// Slice P1: the shared connect helper for this project's real-process tests (ThinClientAgainstTheRunning-
// ServerTests, HookVerbAgainstTheRunningServerTests, BinCliThinClientMatchesBinCliOldTests,
// ServerHandsOverToTheCurrentBuildTests, PipeTransportRealProcessTests). Not Grimora.Cli.Tools.PipeConnection:
// this project references Grimora.Cli for build order only (ReferenceOutputAssembly="false", so its types are
// not usable here — see HooksEndpointTests's comment on why).
internal static class PipeTestClient
{
    public static HttpClient CreateClient(string dataDir, TimeSpan timeout)
    {
        SocketsHttpHandler handler = new()
        {
            ConnectCallback = async (_, ct) =>
            {
                if (OperatingSystem.IsWindows())
                {
                    NamedPipeClientStream pipe = new(".", ServerAddress.PipeName(dataDir), PipeDirection.InOut, PipeOptions.Asynchronous);
                    await pipe.ConnectAsync((int)timeout.TotalMilliseconds, ct);
                    return pipe;
                }
                Socket socket = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(ServerAddress.SocketPath(dataDir)), ct);
                return new NetworkStream(socket, ownsSocket: true);
            },
            ConnectTimeout = timeout,
        };
        return new HttpClient(handler) { BaseAddress = new Uri("http://grimora-pipe.local/"), Timeout = timeout };
    }
}
