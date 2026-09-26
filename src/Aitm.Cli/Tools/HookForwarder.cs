using System.Net;
using System.Text;

namespace Aitm.Cli.Tools;

/// <summary>
/// `aitm hook &lt;event&gt;` for the events whose handlers need Memory, Docs, Graph and Store (SessionEnd,
/// PostToolUse): Aitm.Cli links none of those (CliReferencesNothingInAitm), so the payload goes to the server's
/// <c>POST /hooks/{event}</c> with the project in the <c>Claude-Project-Dir</c> header and no token (AITM
/// holds no secret), and the answer body is printed as it is (RESTRUCTURE.md slice 30).
///
/// A hook fails open: a server that is down, a non-200 answer or any error prints nothing.
/// It never starts the server (SessionStart does that). The connect gives up after
/// <see cref="ConnectTimeout"/>: on Windows a closed loopback port is only refused after about 3 s of
/// retries, and SessionEnd hooks share a 1.5 s budget. The whole call ends at the event's entry in
/// <see cref="Deadlines"/>, so a server that accepts and never answers cannot keep the hook alive.
/// </summary>
public static class HookForwarder
{
    /// <summary>
    /// The events this CLI sends to the server, each with the longest a call may take in total. Each equals the
    /// timeout of the hooks.json slot that runs the event's handlers (HookCommandTests), because an async command
    /// hook's own timeout is not enforced in an interactive session.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, TimeSpan> Deadlines = new Dictionary<string, TimeSpan>
    {
        ["SessionEnd"] = TimeSpan.FromSeconds(390),
        ["PostToolUse"] = TimeSpan.FromSeconds(65),
    };

    /// <summary>A loopback connect to a running server takes well under a millisecond.</summary>
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromMilliseconds(500);

    public static string Forward(string eventName, string payload, int port, string? projectDirEnv, TimeSpan deadline)
    {
        try
        {
            // The deadline covers the whole call: connect, send, the server's work and reading the answer.
            using CancellationTokenSource deadlineSource = new(deadline);
            using SocketsHttpHandler handler = new() { ConnectTimeout = deadline < ConnectTimeout ? deadline : ConnectTimeout };
            using HttpClient client = new(handler) { Timeout = Timeout.InfiniteTimeSpan };
            using HttpRequestMessage request = new(HttpMethod.Post, $"http://127.0.0.1:{port}/hooks/{Uri.EscapeDataString(eventName)}");
            if (!string.IsNullOrWhiteSpace(projectDirEnv)) request.Headers.TryAddWithoutValidation("Claude-Project-Dir", projectDirEnv);
            request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

            using HttpResponseMessage response = client.Send(request, deadlineSource.Token);
            if (response.StatusCode != HttpStatusCode.OK) return "";
            return response.Content.ReadAsStringAsync(deadlineSource.Token).GetAwaiter().GetResult();
        }
        catch
        {
            // fail open: a hook must never block or crash a session
            return "";
        }
    }

    /// <summary>The real call: the port Aitm.Server uses, and the project Claude Code gave this hook.</summary>
    public static string ForwardDefault(string eventName, string payload) => Forward(
        eventName,
        payload,
        ServerAutoStart.DefaultPort(),
        Environment.GetEnvironmentVariable("CLAUDE_PROJECT_DIR"),
        Deadlines[eventName]);
}
