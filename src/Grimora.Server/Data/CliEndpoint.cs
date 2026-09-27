using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Grimora.Server.Data;

/// <summary>
/// RESTRUCTURE.md "Slice 29c: POST /cli on the server." Body <c>{ "args": [...], "cwd": "..." }</c>, answer
/// <c>{ "exitCode": n, "stdout": "...", "stderr": "..." }</c>, always 200 once past the auth middleware.
///
/// The project comes from <see cref="RequestProjectResolver"/> (an <c>--instance</c> in the args is the explicit
/// instance, the body <c>cwd</c> the request's own cwd); the server's own env is never read. The verb runs through
/// <see cref="CliDispatch.RunOnStore"/> on the project's one open connection, under its writer gate.
///
/// Every exception becomes exit 1 with one short stderr line. A call that does not finish in time answers
/// exit 124; a verb cannot be cancelled mid-statement, so it keeps running on its own thread and holds the
/// gate until it really ends, which keeps the shared connection single-user.
/// </summary>
internal static class CliEndpoint
{
    public const int TimeoutExitCode = 124;
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);
    // init.mjs waits 900 s for a large chat import (index-chat); this must never be shorter than that,
    // or the server answers exit 124 for an import the caller was still willing to wait out.
    public static readonly TimeSpan LongTimeout = TimeSpan.FromSeconds(900);

    /// <summary>The verbs in CliDispatch that walk a tree, a transcript directory, another store or the
    /// whole store, and can run for minutes (the SessionEnd doc indexer has run for minutes).</summary>
    private static readonly HashSet<string> LongVerbs = new(StringComparer.Ordinal)
    {
        "index-chat", "index-docs", "index-memory", "index-packages", "import", "redact-chat",
        "recompact-docs", "backup", "spine-import", "seed-edges",
    };

    private static readonly HashSet<string> LongBrainVerbs = new(StringComparer.Ordinal)
    {
        "seed", "learn-batch", "tidy", "distill",
    };

    /// <summary>Runs one verb on the project's open connection. A seam so a test can hold a verb past its timeout.</summary>
    internal delegate int VerbRunner(string[] args, string cwd, TextWriter stdout, TextWriter stderr,
        string instance, string dataDir, SqliteConnection connection);

    public static Task<IResult> Handle(HttpContext context, ProjectStore store, string dataDir, IdleExit idleExit) =>
        Handle(context, store, dataDir, idleExit, CliDispatch.RunOnStore, null);

    internal static async Task<IResult> Handle(HttpContext context, ProjectStore store, string dataDir, IdleExit idleExit,
        VerbRunner runVerb, TimeSpan? timeoutOverride)
    {
        try
        {
            using JsonDocument body = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
            if (!body.RootElement.TryGetProperty("args", out JsonElement argsElement) || argsElement.ValueKind != JsonValueKind.Array)
                return Answer(2, "", "error: the body needs \"args\": [...].");
            string[] args = [.. argsElement.EnumerateArray().Select(e => e.GetString() ?? "")];
            string? bodyCwd = body.RootElement.TryGetProperty("cwd", out JsonElement cwdElement) && cwdElement.ValueKind == JsonValueKind.String
                ? cwdElement.GetString()
                : null;

            string cwd = !string.IsNullOrWhiteSpace(bodyCwd) ? bodyCwd
                : RequestProjectResolver.ProjectDirHeaderOf(context) ?? Directory.GetCurrentDirectory();
            string instance = RequestProjectResolver.Resolve(context, CliDispatch.FlagValue(args, "--instance"), bodyCwd);
            if (instance.Length == 0 || instance is "." or ".." || instance.IndexOfAny(['/', '\\']) >= 0)
                return Answer(2, "", $"error: bad instance name '{instance}'.");

            TimeSpan timeout = timeoutOverride ?? TimeoutFor(args);
            Stopwatch clock = Stopwatch.StartNew();
            ProjectHandle handle = store.Acquire(instance);
            if (!await handle.Gate.WaitAsync(timeout, context.RequestAborted))
                return Answer(TimeoutExitCode, "",
                    $"error: timed out after {timeout.TotalSeconds:0} s waiting for project '{instance}' (another call holds it).");

            StringWriter stdout = new();
            StringWriter stderr = new();
            Task<int> work;
            // The verb may outlive this request (exit 124 below) and keeps the gate and the store until it ends,
            // so it counts as a call in flight of its own: the idle exit and the shutdown drain wait for it.
            IDisposable orphanGuard = idleExit.Begin();
            try
            {
                work = Task.Run(() => RunHoldingGate(runVerb, handle, orphanGuard, args, cwd, stdout, stderr, instance, dataDir));
            }
            catch
            {
                orphanGuard.Dispose();
                handle.Gate.Release();
                throw;
            }

            TimeSpan left = timeout - clock.Elapsed;
            if (left < TimeSpan.Zero) left = TimeSpan.Zero;
            using CancellationTokenSource delayCancel = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            Task finished = await Task.WhenAny(work, Task.Delay(left, delayCancel.Token));
            await delayCancel.CancelAsync();
            if (finished != work)
                return Answer(TimeoutExitCode, "",
                    $"error: timed out after {timeout.TotalSeconds:0} s; '{string.Join(' ', args.Take(2))}' is still running on the server and holds project '{instance}' until it ends.");

            int exit = await work;
            return Answer(exit, stdout.ToString(), stderr.ToString());
        }
        catch (Exception ex)
        {
            return Answer(1, "", $"error: {ex.GetType().Name}: {FirstLine(ex.Message)}");
        }
    }

    /// <summary>Runs on a pool thread and releases the gate only when the verb has really ended.</summary>
    private static int RunHoldingGate(VerbRunner runVerb, ProjectHandle handle, IDisposable orphanGuard, string[] args, string cwd,
        StringWriter stdout, StringWriter stderr, string instance, string dataDir)
    {
        try
        {
            return runVerb(args, cwd, stdout, stderr, instance, dataDir, handle.Connection);
        }
        catch (Exception ex)
        {
            stderr.WriteLine($"error: {ex.GetType().Name}: {FirstLine(ex.Message)}");
            return 1;
        }
        finally
        {
            // A verb that threw after its own BEGIN would leave the shared connection mid-transaction for
            // every later /mcp and /cli call. With no transaction open this throws, which is the normal case.
            try
            {
                using SqliteCommand rollback = handle.Connection.CreateCommand();
                rollback.CommandText = "ROLLBACK";
                rollback.ExecuteNonQuery();
            }
            catch (SqliteException) { }
            catch (InvalidOperationException) { }
            handle.Gate.Release();
            orphanGuard.Dispose();
        }
    }

    private static TimeSpan TimeoutFor(string[] args)
    {
        if (int.TryParse(Environment.GetEnvironmentVariable("Grimora_CLI_TIMEOUT_SECONDS"), out int seconds) && seconds > 0)
            return TimeSpan.FromSeconds(seconds);
        string verb = args.Length > 0 ? args[0] : "help";
        bool isLong = LongVerbs.Contains(verb)
            || (verb == "brain" && args.Skip(1).FirstOrDefault(s => !s.StartsWith("--", StringComparison.Ordinal)) is { } sub && LongBrainVerbs.Contains(sub));
        return isLong ? LongTimeout : DefaultTimeout;
    }

    private static string FirstLine(string text)
    {
        int end = text.IndexOfAny(['\r', '\n']);
        string line = end >= 0 ? text[..end] : text;
        return line.Length > 200 ? line[..200] : line;
    }

    private static IResult Answer(int exitCode, string stdout, string stderr) =>
        Results.Json(new { exitCode, stdout, stderr });
}
