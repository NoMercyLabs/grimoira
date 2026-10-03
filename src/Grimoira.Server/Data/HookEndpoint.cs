using System.Text;
using System.Text.Json.Nodes;
using Grimoira.Hooks.Tools;

namespace Grimoira.Server.Data;

/// <summary>
/// RESTRUCTURE.md "Slice 34: POST /hooks/{event} on the server." The body is the Claude Code hook JSON
/// (the payload <c>grimoira hook &lt;event&gt;</c> reads on stdin); the answer is the handler's stdout, 200.
/// The project comes from <see cref="RequestProjectResolver"/> (the payload's <c>cwd</c> is the request's own
/// cwd), the same rule as /mcp and /cli, and the handler runs under that project's one writer gate
/// (<see cref="ProjectHandle.Gate"/>), the same gate every /mcp tool call takes.
///
/// A hook must fail open: an unknown event, a malformed payload or a handler error all answer 200 with
/// an empty body, never a 5xx.
///
/// SessionEnd's three indexing handlers do not run inline any more (slice 38): an <c>http</c> hook cannot
/// be async and is bound by the 1.5 s SessionEnd budget (RESTRUCTURE.md phase 4 facts), and running them
/// synchronously meant a slow or failing indexer either blocked the answer or, wrapped to avoid that, had
/// its failure silently thrown away. <see cref="IndexJobQueue"/> takes the payload instead and answers at
/// once; the actual indexing runs in the background, under the project's own writer gate, with a failure
/// that survives every retry recorded as a finding instead of vanishing.
/// </summary>
internal static class HookEndpoint
{
    public static async Task<IResult> Handle(string eventName, HttpContext context, ProjectStore store, IndexJobQueue indexQueue)
    {
        string output;
        try
        {
            string body;
            using (StreamReader reader = new(context.Request.Body, Encoding.UTF8))
            {
                body = await reader.ReadToEndAsync(context.RequestAborted);
            }

            if (JsonNode.Parse(body) is not JsonObject payload) return Empty();

            // A command hook gets CLAUDE_PROJECT_DIR from Claude Code. The shared server's own env is the env of
            // whichever session started it, so the handlers get the request's project instead: the
            // Claude-Project-Dir header, else the payload's cwd, else the server's directory, the same order
            // RequestProjectResolver uses for the gate. Handler and gate agree on the project.
            string? payloadCwd = payload["cwd"] is JsonValue cwdValue && cwdValue.TryGetValue(out string? s) ? s : null;
            string projectDir = RequestProjectResolver.ProjectDirHeaderOf(context)
                ?? (string.IsNullOrWhiteSpace(payloadCwd) ? null : payloadCwd)
                ?? Directory.GetCurrentDirectory();
            string instance = RequestProjectResolver.Resolve(context, requestCwd: payloadCwd);

            if (eventName == "SessionEnd")
            {
                // Queued, not run here: see the class doc comment and IndexJobQueue. Still answers empty,
                // same as the handlers themselves always did.
                indexQueue.Enqueue(instance, body);
                return Empty();
            }

            IReadOnlyList<Func<string, string?, string>> handlers = HandlersFor(eventName, payload);
            if (handlers.Count == 0) return Empty();

            ProjectHandle handle = store.Acquire(instance);
            await handle.Gate.WaitAsync(context.RequestAborted);
            try
            {
                StringBuilder combined = new();
                foreach (Func<string, string?, string> handler in handlers)
                {
                    try
                    {
                        combined.Append(handler(body, projectDir));
                    }
                    catch
                    {
                        // fail open: one handler's error never stops the next or the session
                    }
                }
                output = combined.ToString();
            }
            finally
            {
                handle.Gate.Release();
            }
        }
        catch
        {
            // fail open: a hook must never block or crash a session
            return Empty();
        }

        return Results.Text(output, output.StartsWith('{') ? "application/json" : "text/plain", Encoding.UTF8);
    }

    /// <summary>The event map: the slice 20-22 handlers, in the order hooks.json runs their slots.</summary>
    /// <remarks>PatternWatch does not take the project yet: it gets it when its own slot moves to http.
    /// SessionEnd is handled directly in <see cref="Handle"/> (queued), never through this map.</remarks>
    private static IReadOnlyList<Func<string, string?, string>> HandlersFor(string eventName, JsonObject payload)
    {
        switch (eventName)
        {
            case "PreCompact":
                return [CompactBriefTool.Execute];
            case "UserPromptSubmit":
                return [CompactRestoreTool.Execute];
            case "Stop":
                return [StopFlushTool.Execute];
            case "PostToolUse":
                string? toolName = payload["tool_name"] is JsonValue v && v.TryGetValue(out string? t) ? t : null;
                return toolName switch
                {
                    "Bash" or "PowerShell" or "Read" or "Grep" or "Glob" => [Ignore(PatternWatchTool.Execute)],
                    "Write" or "Edit" or "MultiEdit" or "NotebookEdit" => [IndexOnEditTool.Execute],
                    _ => [],
                };
            default:
                return [];
        }
    }

    private static Func<string, string?, string> Ignore(Func<string, string> handler) => (stdin, _) => handler(stdin);

    private static IResult Empty() => Results.Text("", "text/plain", Encoding.UTF8);
}
