using Grimora.Store.Data;

namespace Grimora.Server.Data;

/// <summary>
/// The one rule the shared server uses to answer "which project" for /mcp, /cli and /hooks, in this order:
/// an explicit instance (an <c>--instance</c> arg on /cli, else the <c>Grimora-Instance</c> header), then the
/// <c>Claude-Project-Dir</c> header, then the request's own cwd (the /cli body <c>cwd</c>, the /hooks payload
/// <c>cwd</c>), then the server's current directory. The two headers are the header-cased form of the
/// Grimora_INSTANCE / CLAUDE_PROJECT_DIR env vars the stdio hosts (mcp.cs, grimora.cs) read, and the name goes
/// through <see cref="StoreConnection.ResolveInstance(string?, string?, string)"/> so every host slugs it the
/// same way.
///
/// It never reads the server process's own environment. The server is one process serving every session;
/// started from a session hook it inherits that session's CLAUDE_PROJECT_DIR, and an env fallback would pin
/// every other session's request to that project.
/// </summary>
public static class RequestProjectResolver
{
    public const string InstanceHeader = "Grimora-Instance";
    public const string ProjectDirHeader = "Claude-Project-Dir";

    /// <param name="context">The request; null only outside a request, which leaves the server's directory.</param>
    /// <param name="explicitInstance">An instance the request named itself (/cli's <c>--instance</c>). It is
    /// returned verbatim, as the CLI does; the caller validates it.</param>
    /// <param name="requestCwd">The cwd the request carries in its body, if any.</param>
    public static string Resolve(HttpContext? context, string? explicitInstance = null, string? requestCwd = null)
    {
        if (explicitInstance is not null) return explicitInstance;

        string? instanceHeader = context?.Request.Headers[InstanceHeader].FirstOrDefault();
        string? projectDir = ProjectDirHeaderOf(context);
        if (string.IsNullOrWhiteSpace(projectDir)) projectDir = requestCwd;

        return StoreConnection.ResolveInstance(instanceHeader, projectDir, Directory.GetCurrentDirectory());
    }

    /// <summary>The <c>Claude-Project-Dir</c> header, or null when the request did not send one.</summary>
    internal static string? ProjectDirHeaderOf(HttpContext? context)
    {
        string? value = context?.Request.Headers[ProjectDirHeader].FirstOrDefault();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
