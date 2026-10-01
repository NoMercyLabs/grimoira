using Grimoira.Brain.Data;
using Grimoira.Store.Tools;

namespace Grimoira.Server.Handover;

/// <summary>
/// Find existing NoMercy tools by purpose (browser login, native code search, CI watching, process
/// ownership). Ported from mcp.cs's <c>workspace_capabilities</c> (mcp.cs:317), one of the 3 handover
/// tools RESTRUCTURE.md slice 23 moves into <c>Grimoira.Server/Handover</c>. Read-only discovery: never
/// executes the found tools or accesses credentials. The one external call — the workspace lookup script
/// — goes through the injected <see cref="IProcessRunner"/> so a test never spawns a real Python process.
/// The live mcp.cs copy keeps serving this tool unchanged until phase 3 swaps the host over
/// (RESTRUCTURE.md section 0). This is the first test <c>workspace_capabilities</c> has had.
/// </summary>
public sealed class WorkspaceCapabilitiesTool : ITool
{
    public string Name => "workspace-capabilities";
    public string CliVerb => "workspace-capabilities";
    public string McpName => "workspace_capabilities";
    public string Help =>
        "workspace-capabilities <query>   find existing NoMercy tools by purpose; returns a few source " +
        "paths with reviewed prerequisites, never executes what it finds.";

    /// <summary>25 seconds, matching mcp.cs's <c>workspace_capabilities</c> call to
    /// <c>RunWorkspacePython</c> (mcp.cs:317-320).</summary>
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(25);

    public string Execute(string query, string projectRoot, IProcessRunner runner, TimeSpan? timeout = null)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length > 1000)
            return "Provide a task description between 1 and 1000 characters.";

        string script = Path.Combine(projectRoot, "scripts", "workspace-capabilities.py");
        if (!File.Exists(script)) script = Path.Combine(projectRoot, ".claude", "scripts", "workspace-capabilities.py");
        if (!File.Exists(script)) return "Workspace lookup unavailable: set CLAUDE_PROJECT_DIR to the NoMercy workspace root.";

        List<string> args = ["--limit", "3", "--", query];
        return HandoverProcess.RunPython(runner, script, args, projectRoot, "Workspace lookup", timeout ?? DefaultTimeout);
    }
}
