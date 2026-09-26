using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace Aitm.Cli.Tools;

/// <summary>
/// `aitm hook &lt;event&gt;` for the events whose handlers need Memory, Docs, Graph and Store (SessionEnd,
/// PostToolUse): Aitm.Cli links none of those (CliReferencesNothingInAitm), so the payload goes to the server's
/// <c>POST /hooks/{event}</c> with the bearer token from <c>server.token</c> and the project in the
/// <c>Claude-Project-Dir</c> header, and the answer body is printed as it is (RESTRUCTURE.md slice 30).
///
/// A hook fails open: a server that is down, a refused token, a non-200 answer or any error prints nothing.
/// It never starts the server (SessionStart does that). The connect gives up after
/// <see cref="ConnectTimeout"/>: on Windows a closed loopback port is only refused after about 3 s of
/// retries, and SessionEnd hooks share a 1.5 s budget. Once connected there is no client timeout; the slot's
/// own timeout in hooks.json ends a call that runs too long.
/// </summary>
internal static class HookForwarder
{
    /// <summary>A loopback connect to a running server takes well under a millisecond.</summary>
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromMilliseconds(500);

    public static string Forward(string eventName, string payload, int port, string dataDir, string? projectDirEnv)
    {
        try
        {
            using SocketsHttpHandler handler = new() { ConnectTimeout = ConnectTimeout };
            using HttpClient client = new(handler) { Timeout = Timeout.InfiniteTimeSpan };
            using HttpRequestMessage request = new(HttpMethod.Post, $"http://127.0.0.1:{port}/hooks/{Uri.EscapeDataString(eventName)}");
            string token = ServerHeadersCommand.ReadToken(dataDir);
            if (token.Length > 0) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (!string.IsNullOrWhiteSpace(projectDirEnv)) request.Headers.TryAddWithoutValidation("Claude-Project-Dir", projectDirEnv);
            request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

            using HttpResponseMessage response = client.Send(request);
            if (response.StatusCode != HttpStatusCode.OK) return "";
            return response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        }
        catch
        {
            // fail open: a hook must never block or crash a session
            return "";
        }
    }

    /// <summary>The real call: the port and data dir Aitm.Server uses, and the project Claude Code gave this hook.</summary>
    public static string ForwardDefault(string eventName, string payload) => Forward(
        eventName,
        payload,
        ServerAutoStart.DefaultPort(),
        ServerHeadersCommand.DefaultDataDir(),
        Environment.GetEnvironmentVariable("CLAUDE_PROJECT_DIR"));
}
