using System.Text;
using System.Text.Json.Nodes;
using Aitm.Hooks.Tools;
using Aitm.Store.Data;
using Microsoft.AspNetCore.Http;

namespace Aitm.Server.Data;

/// <summary>
/// RESTRUCTURE.md "Slice 34: POST /hooks/{event} on the server." The body is the Claude Code hook JSON
/// (the payload <c>aitm hook &lt;event&gt;</c> reads on stdin); the answer is the handler's stdout, 200.
/// The project is resolved like /mcp (<see cref="McpInstanceContext"/>: the <c>Claude-Project-Dir</c>
/// header, else the payload's <c>cwd</c>), and the handler runs under that project's one writer gate
/// (<see cref="ProjectHandle.Gate"/>), the same gate every /mcp tool call takes.
///
/// A hook must fail open: an unknown event, a malformed payload or a handler error all answer 200 with
/// an empty body, never a 5xx.
///
/// SessionEnd runs its three handlers synchronously here. An <c>http</c> hook cannot be async and is
/// bound by the 1.5 s SessionEnd budget (RESTRUCTURE.md phase 4 facts), so slice 38 must enqueue them
/// and answer at once; this slice does not build that queue.
/// </summary>
internal static class HookEndpoint
{
    public static async Task<IResult> Handle(string eventName, HttpContext context, ProjectStore store)
    {
        string output = "";
        try
        {
            string body;
            using (StreamReader reader = new(context.Request.Body, Encoding.UTF8))
            {
                body = await reader.ReadToEndAsync(context.RequestAborted);
            }

            if (JsonNode.Parse(body) is not JsonObject payload) return Empty();

            IReadOnlyList<Func<string, string>> handlers = HandlersFor(eventName, payload);
            if (handlers.Count == 0) return Empty();

            // A command hook inherits CLAUDE_PROJECT_DIR, which every handler prefers over the payload's
            // cwd (HookPaths.ResolveInstance). The shared server has no per-session env, so the header
            // takes that place: it becomes the cwd the handler sees, and handler and gate agree on the
            // project.
            string? projectDirHeader = context.Request.Headers[McpInstanceContext.ProjectDirHeader].FirstOrDefault();
            string instance;
            if (!string.IsNullOrWhiteSpace(projectDirHeader))
            {
                payload["cwd"] = projectDirHeader;
                body = payload.ToJsonString();
                instance = McpInstanceContext.Resolve(context);
            }
            else
            {
                string? cwd = payload["cwd"] is JsonValue cwdValue && cwdValue.TryGetValue(out string? s) ? s : null;
                instance = StoreConnection.ResolveInstance(
                    context.Request.Headers[McpInstanceContext.InstanceHeader].FirstOrDefault(),
                    cwd,
                    Directory.GetCurrentDirectory());
            }

            ProjectHandle handle = store.Acquire(instance);
            await handle.Gate.WaitAsync(context.RequestAborted);
            try
            {
                StringBuilder combined = new();
                foreach (Func<string, string> handler in handlers)
                {
                    try
                    {
                        combined.Append(handler(body));
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
    private static IReadOnlyList<Func<string, string>> HandlersFor(string eventName, JsonObject payload)
    {
        switch (eventName)
        {
            case "PreCompact":
                return [CompactBriefTool.Execute];
            case "UserPromptSubmit":
                return [CompactRestoreTool.Execute];
            case "SessionEnd":
                return [SessionIndexChatTool.Execute, SessionIndexDocsTool.Execute, IndexCodeSessionEndTool.Execute];
            case "PostToolUse":
                string? toolName = payload["tool_name"] is JsonValue v && v.TryGetValue(out string? t) ? t : null;
                return toolName switch
                {
                    "Bash" or "PowerShell" => [PatternWatchTool.Execute],
                    "Write" or "Edit" or "MultiEdit" or "NotebookEdit" => [IndexOnEditTool.Execute],
                    _ => [],
                };
            default:
                return [];
        }
    }

    private static IResult Empty() => Results.Text("", "text/plain", Encoding.UTF8);
}
