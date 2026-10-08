using System.Threading.Channels;
using Grimoira.Facts.Tools;
using Grimoira.Hooks.Tools;
using Microsoft.Data.Sqlite;

namespace Grimoira.Server.Data;

/// <summary>
/// SessionEnd's three indexing handlers (chat, docs, code) used to run synchronously inside the HTTP
/// request, each wrapped so any exception vanished with no trace (HookEndpoint's old <c>Ignore</c>): a
/// slow disk or a transient "database is locked" made a session start with silently missing memory, and
/// nothing said why. This queue moves that work off the request thread so SessionEnd's own answer stays
/// fast (the 1.5 s hooks.json budget, RESTRUCTURE.md), while making sure the work itself either finishes
/// or leaves a record - the same "the service proves what it did or didn't do" rule CleanExitRecord /
/// CallRing already use for a lost call, applied here to a lost index.
///
/// <see cref="Enqueue"/> takes an <see cref="IdleExit"/> "in flight" token (the same one the request
/// pipeline itself uses in Program.cs) before returning, and only releases it once the job has actually
/// finished. A shutdown's drain (<see cref="IdleExit.WaitForDrain"/>) and the idle-exit timer therefore
/// both wait for a queued job the same way they already wait for a call still in flight, instead of
/// letting the process exit out from under it.
///
/// A handler that still fails after its own bounded lock-wait (HookStore's 3 s "Default Timeout", set so a
/// hook fails open quickly rather than the store's usual 30 s - exactly the SQLITE_BUSY/SQLITE_LOCKED shape
/// a load-heavy CI run hits) is written to the project's own <c>findings</c> table (Grimoira.Facts'
/// <c>finding</c>/<c>log_finding</c> tool, already the house place for "an unrelated issue spotted during
/// other work, surfaced to the operator later") so `grimoira findings` / <c>open_findings</c> shows it
/// instead of the next session silently starting with gaps.
/// </summary>
public sealed class IndexJobQueue(ProjectStore store, IdleExit idleExit, TimeSpan? gateTimeout = null)
{
    public static readonly TimeSpan DefaultGateTimeout = TimeSpan.FromSeconds(60);

    private readonly Channel<IndexJob> _channel = Channel.CreateUnbounded<IndexJob>(
        new UnboundedChannelOptions { SingleReader = true });

    /// <summary>Queues the SessionEnd body for <paramref name="instance"/> and returns at once; the caller
    /// (HookEndpoint) never waits on the actual indexing. Takes the idle-exit "in flight" token itself so a
    /// job queued right before a shutdown or an idle exit still gets to run (see the class doc comment).</summary>
    public void Enqueue(string instance, string body)
    {
        IDisposable inFlight = idleExit.Begin();
        if (!_channel.Writer.TryWrite(new IndexJob(instance, body, inFlight)))
            inFlight.Dispose(); // unreachable for an unbounded channel; kept so a token is never leaked
    }

    /// <summary>Drains the queue until the process exits; started once from Program.cs and never awaited (a
    /// bug in one job must never stop the next job from running).</summary>
    public async Task RunAsync()
    {
        await foreach (IndexJob job in _channel.Reader.ReadAllAsync())
        {
            try
            {
                await ProcessAsync(job);
            }
            catch
            {
                // a queue worker that dies takes every SessionEnd after it down with it; one job's own
                // bug must never do that. ProcessAsync already records what it can, this is a last resort.
            }
            finally
            {
                job.InFlight.Dispose();
            }
        }
    }

    private async Task ProcessAsync(IndexJob job)
    {
        ProjectHandle handle;
        try
        {
            handle = store.Acquire(job.Instance);
        }
        catch (Exception e)
        {
            // No store to record a finding in either; there is nowhere durable left to put this, so it is
            // the one case still only a trace, same as the review's "no error record" complaint - but the
            // one case is "the project itself is unopenable", which the next /mcp or /cli call on it will
            // hit and report right away, so it does not stay silent for long.
            Console.Error.WriteLine($"IndexJobQueue: could not open project store '{job.Instance}': {e.Message}");
            return;
        }

        // The gate is taken per handler, not across all three, so a write call queued behind this job gets
        // its turn between chat, docs and code indexing instead of after all of them (issue #24).
        await RunHandlerUnderGate(handle, "chat", SessionIndexChatTool.TryExecute, job.Body, gateTimeout ?? DefaultGateTimeout);
        await RunHandlerUnderGate(handle, "docs", SessionIndexDocsTool.TryExecute, job.Body, gateTimeout ?? DefaultGateTimeout);
        await RunHandlerUnderGate(handle, "code", IndexCodeSessionEndTool.TryExecute, job.Body, gateTimeout ?? DefaultGateTimeout);
    }

    private static async Task RunHandlerUnderGate(ProjectHandle handle, string name, Func<string, Exception?> handler, string body, TimeSpan timeout)
    {
        if (!await handle.Gate.WaitAsync(timeout))
        {
            Console.Error.WriteLine($"IndexJobQueue: SessionEnd '{name}' skipped after waiting {timeout.TotalSeconds:0} s for the project gate.");
            return;
        }
        try
        {
            RunHandler(handle.Connection, name, handler, body);
        }
        finally
        {
            handle.Gate.Release();
        }
    }

    private static void RunHandler(SqliteConnection connection, string name, Func<string, Exception?> handler, string body)
    {
        // One attempt: HookStore.Open already gives the handler its own bounded wait for a held lock (3 s,
        // set precisely so a hook fails open quickly instead of the store's usual 30 s - see HookStore's own
        // doc comment). Retrying on top of that would only multiply an already-deliberate wait; this layer's
        // job is the other half - a failure that survives that wait must be recorded, not thrown away.
        Exception? error = handler(body);
        if (error is null) return;

        try
        {
            new FindingTool().ExecuteMcp(connection,
                title: $"SessionEnd indexing failed: {name}",
                detail: error.ToString(),
                source: "hook:SessionEnd");
        }
        catch
        {
            Console.Error.WriteLine($"IndexJobQueue: SessionEnd '{name}' indexing failed and the finding itself could not be recorded: {error}");
        }
    }

    private readonly record struct IndexJob(string Instance, string Body, IDisposable InFlight);
}
