using System.Net.Http;

namespace Aitm.Cli.Tools;

/// <summary>
/// The one retry loop every client of the service shares (ThinClient, HookForwarder, McpBridge). The service
/// exits by itself when idle, so a client can meet it half gone: the listener is closed but the process still
/// holds server.lock. A refused connection is followed by <c>ensureServer</c> (which starts a service when none
/// answers /health; a new service that meets the old one's lock exits at once, cleanly) and a resend, up to
/// <see cref="MaxResends"/> times, <see cref="Spacing"/> apart. Only a refused connection is resent: any other
/// failure may mean the call already ran, so it is never sent twice.
/// </summary>
public static class RefusedConnectionRetry
{
    public const int MaxResends = 3;
    public static readonly TimeSpan Spacing = TimeSpan.FromMilliseconds(200);

    /// <summary>True with the answer, or false when the service stayed unreachable after every resend. Any failure
    /// that is not a refused connection propagates.</summary>
    public static bool TrySend<T>(Func<T> attempt, Func<bool> ensureServer, out T? result)
    {
        for (int resend = 0; ; resend++)
        {
            try
            {
                result = attempt();
                return true;
            }
            catch (HttpRequestException ex) when (ex.HttpRequestError == HttpRequestError.ConnectionError)
            {
                if (resend == MaxResends)
                {
                    result = default;
                    return false;
                }
                ensureServer();
                Thread.Sleep(Spacing);
            }
        }
    }

    /// <summary>The same loop for an async call: (reached, answer).</summary>
    public static async Task<(bool Reached, T? Result)> TrySendAsync<T>(Func<Task<T>> attempt, Func<bool> ensureServer, CancellationToken cancellationToken)
    {
        for (int resend = 0; ; resend++)
        {
            try
            {
                return (true, await attempt());
            }
            catch (HttpRequestException ex) when (ex.HttpRequestError == HttpRequestError.ConnectionError)
            {
                if (resend == MaxResends) return (false, default);
                ensureServer();
                await Task.Delay(Spacing, cancellationToken);
            }
        }
    }
}
