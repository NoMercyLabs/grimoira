using System.Diagnostics;
using System.Text.Json;
using Aitm.Store.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;

namespace Aitm.Server.Data;

/// <summary>
/// RESTRUCTURE.md "Slice 29c: POST /cli on the server." Body <c>{ "args": [...], "cwd": "..." }</c>, answer
/// <c>{ "exitCode": n, "stdout": "...", "stderr": "..." }</c>, always 200 once past the auth middleware.
///
/// The project: an <c>--instance</c> in the args wins (as on the CLI), then the <c>Aitm-Instance</c> header,
/// then the <c>Claude-Project-Dir</c> header, then the body <c>cwd</c>. Unlike <see cref="McpInstanceContext"/>
/// the server's own AITM_INSTANCE / CLAUDE_PROJECT_DIR are never read, so a server started from one project
/// cannot pin every request to it (the slice 34 gap). The verb runs through
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
    public static readonly TimeSpan LongTimeout = TimeSpan.FromSeconds(600);

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

    public static async Task<IResult> Handle(HttpContext context, ProjectStore store, string dataDir)
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

            string? instanceHeader = context.Request.Headers[McpInstanceContext.InstanceHeader].FirstOrDefault();
            string? projectDirHeader = context.Request.Headers[McpInstanceContext.ProjectDirHeader].FirstOrDefault();
            string cwd = !string.IsNullOrWhiteSpace(bodyCwd) ? bodyCwd
                : !string.IsNullOrWhiteSpace(projectDirHeader) ? projectDirHeader
                : Directory.GetCurrentDirectory();
            string instance = FlagValue(args, "--instance")
                ?? StoreConnection.ResolveInstance(instanceHeader, projectDirHeader, cwd);
            if (instance.Length == 0 || instance is "." or ".." || instance.IndexOfAny(['/', '\\']) >= 0)
                return Answer(2, "", $"error: bad instance name '{instance}'.");

            TimeSpan timeout = TimeoutFor(args);
            Stopwatch clock = Stopwatch.StartNew();
            ProjectHandle handle = store.Acquire(instance);
            if (!await handle.Gate.WaitAsync(timeout, context.RequestAborted))
                return Answer(TimeoutExitCode, "",
                    $"error: timed out after {timeout.TotalSeconds:0} s waiting for project '{instance}' (another call holds it).");

            StringWriter stdout = new();
            StringWriter stderr = new();
            Task<int> work;
            try
            {
                work = Task.Run(() => RunHoldingGate(handle, args, cwd, stdout, stderr, instance, dataDir));
            }
            catch
            {
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
    private static int RunHoldingGate(ProjectHandle handle, string[] args, string cwd, StringWriter stdout, StringWriter stderr,
        string instance, string dataDir)
    {
        try
        {
            return CliDispatch.RunOnStore(args, cwd, stdout, stderr, instance, dataDir, handle.Connection);
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
        }
    }

    private static TimeSpan TimeoutFor(string[] args)
    {
        if (int.TryParse(Environment.GetEnvironmentVariable("AITM_CLI_TIMEOUT_SECONDS"), out int seconds) && seconds > 0)
            return TimeSpan.FromSeconds(seconds);
        string verb = args.Length > 0 ? args[0] : "help";
        bool isLong = LongVerbs.Contains(verb)
            || (verb == "brain" && args.Skip(1).FirstOrDefault(s => !s.StartsWith("--", StringComparison.Ordinal)) is string sub && LongBrainVerbs.Contains(sub));
        return isLong ? LongTimeout : DefaultTimeout;
    }

    /// <summary>The same rule as CliDispatch's GetFlag: the token after the flag, whatever it is.</summary>
    private static string? FlagValue(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
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
