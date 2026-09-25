using Aitm.Store.Data;
using Microsoft.AspNetCore.Http;

namespace Aitm.Server.Data;

/// <summary>
/// Project-per-session resolution (RESTRUCTURE.md "Slice 26": "resolved the way mcp.cs's
/// ResolveInstance does today"). mcp.cs's <c>ResolveInstance</c> reads <c>AITM_INSTANCE</c> /
/// <c>CLAUDE_PROJECT_DIR</c> from the process environment and falls back to the current directory
/// (mcp.cs:325) — that works for the stdio host because every session gets its own process with its
/// own inherited environment. The HTTP host is one shared process serving every session at once, so the
/// same two inputs travel as request headers instead (<c>Aitm-Instance</c> / <c>Claude-Project-Dir</c>,
/// the header-cased form of the two env vars), read through <see cref="StoreConnection"/>'s existing
/// injectable overload so both hosts apply the exact same slugging rule. A request that sends neither
/// header falls back to the server process's own environment and current directory, matching mcp.cs's
/// behaviour for a caller that sets nothing.
/// </summary>
public static class McpInstanceContext
{
    public const string InstanceHeader = "Aitm-Instance";
    public const string ProjectDirHeader = "Claude-Project-Dir";

    public static string Resolve(HttpContext? context)
    {
        string? instanceHeader = context?.Request.Headers[InstanceHeader].FirstOrDefault();
        string? projectDirHeader = context?.Request.Headers[ProjectDirHeader].FirstOrDefault();

        string? aitmInstanceEnv = !string.IsNullOrWhiteSpace(instanceHeader)
            ? instanceHeader
            : Environment.GetEnvironmentVariable("AITM_INSTANCE");
        string? claudeProjectDirEnv = !string.IsNullOrWhiteSpace(projectDirHeader)
            ? projectDirHeader
            : Environment.GetEnvironmentVariable("CLAUDE_PROJECT_DIR");

        return StoreConnection.ResolveInstance(aitmInstanceEnv, claudeProjectDirEnv, Directory.GetCurrentDirectory());
    }
}
