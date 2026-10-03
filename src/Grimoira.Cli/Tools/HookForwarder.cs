using System.Net;
using System.Text;

namespace Grimoira.Cli.Tools;

/// <summary>
/// `grimoira hook &lt;event&gt;` for the events whose handlers need Memory, Docs, Graph and Store (SessionEnd,
/// PostToolUse): Grimoira.Cli links none of those (CliReferencesNothingInGrimoira), so the payload goes to the server's
/// <c>POST /hooks/{event}</c> with the project in the <c>Claude-Project-Dir</c> header and no token (Grimoira
/// holds no secret), and the answer body is printed as it is (RESTRUCTURE.md slice 30).
///
/// A hook fails open: a server that is down, a non-200 answer or any error prints nothing.
/// A server that is not there is started silently once, within <see cref="ServerAutoStart.DefaultMaxWait"/>,
/// and the payload is sent again. The connect gives up after
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
        // Stop only reads/writes one small ledger file and, at most, flushes it into the store — nowhere
        // near SessionEnd's indexing work — so it gets a short budget of its own.
        ["Stop"] = TimeSpan.FromSeconds(10),
        // The prompt recall runs on every prompt: a small slice of its 10 s slot, so a slow or absent server
        // never makes the user wait for a prompt to be accepted.
        ["UserPromptSubmit"] = TimeSpan.FromSeconds(3),
    };

    /// <summary>A pipe/socket connect to a running server takes well under a millisecond.</summary>
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromMilliseconds(500);

    public static string Forward(string eventName, string payload, string dataDir, string? projectDirEnv, TimeSpan deadline, Func<bool>? ensureServer = null)
    {
        try
        {
            // The deadline covers the whole call: connect, send, the server's work and reading the answer.
            using CancellationTokenSource deadlineSource = new(deadline);
            // No client timeout: the deadline token below covers the whole call.
            using HttpClient client = PipeConnection.CreateClient(dataDir, deadline < ConnectTimeout ? deadline : ConnectTimeout, Timeout.InfiniteTimeSpan);
            // All waits share the hook's deadline; the whole flow is also bounded by it below.
            LostCallGuard guard = new(dataDir, deadline);
            HttpRequestMessage NewRequest()
            {
                HttpRequestMessage request = new(HttpMethod.Post, $"/hooks/{Uri.EscapeDataString(eventName)}");
                request.Headers.TryAddWithoutValidation(RefusedConnectionRetry.CallIdHeader, guard.CallId);
                if (!string.IsNullOrWhiteSpace(projectDirEnv)) request.Headers.TryAddWithoutValidation("Claude-Project-Dir", projectDirEnv);
                request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
                return request;
            }

            HttpResponseMessage Send()
            {
                using HttpRequestMessage request = NewRequest();
                return client.Send(request, HttpCompletionOption.ResponseHeadersRead, deadlineSource.Token);
            }

            // Nobody home: start the service (silently, bounded) and send the payload again, up to 3 times
            // (RefusedConnectionRetry). With no ensureServer the hook is sent once.
            string Flow()
            {
                HttpResponseMessage? response;
                if (ensureServer is null) response = Send();
                else if (!RefusedConnectionRetry.TrySend(Send, ensureServer, out response, guard)) return "";

                using (response)
                {
                    if (response!.StatusCode != HttpStatusCode.OK) return "";
                    return response.Content.ReadAsStringAsync(deadlineSource.Token).GetAwaiter().GetResult();
                }
            }

            // The start of a new service and the waits for the old one do not take the token, so the whole flow runs
            // on its own task and the hook ends at its deadline whatever it is waiting on.
            Task<string> flow = Task.Run(Flow);
            return flow.Wait(deadline) ? flow.Result : "";
        }
        catch
        {
            // fail open: a hook must never block or crash a session
            return "";
        }
    }

    /// <summary>The real call: the data dir Grimoira.Server uses, and the project Claude Code gave this hook.</summary>
    public static string ForwardDefault(string eventName, string payload) => Forward(
        eventName,
        payload,
        ServerAutoStart.DefaultDataDir(),
        Environment.GetEnvironmentVariable("CLAUDE_PROJECT_DIR"),
        Deadlines[eventName],
        ServerAutoStart.EnsureRunning);
}
