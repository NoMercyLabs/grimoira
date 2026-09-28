using Grimora.Brain.Data;
using Grimora.Store.Tools;

namespace Grimora.Server.Handover;

/// <summary>
/// Search fixed text or filenames in one registered NoMercy repository. Ported from mcp.cs's
/// <c>workspace_search</c> (mcp.cs:327), one of the 3 handover tools RESTRUCTURE.md slice 23 moves into
/// <c>Grimora.Server/Handover</c>. The Python script it shells out to (<c>workspace-search.py</c>) is the
/// single origin for the search itself and is untouched; the one external call goes through the injected
/// <see cref="IProcessRunner"/> so a test never spawns a real Python process. The live mcp.cs copy keeps
/// serving this tool unchanged until phase 3 swaps the host over (RESTRUCTURE.md section 0).
/// </summary>
public sealed class WorkspaceSearchTool : ITool
{
    public string Name => "workspace-search";
    public string CliVerb => "workspace-search";
    public string McpName => "workspace_search";
    public string Help =>
        "workspace-search --repo <repo> --pattern <text> [--path <dir>] [--names]   search fixed text or " +
        "filenames in one registered NoMercy repository; '.' means the root.";

    /// <summary>20 seconds, matching mcp.cs's <c>workspace_search</c> call to <c>RunWorkspacePython</c>
    /// (mcp.cs:327-333).</summary>
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(20);

    public string Execute(string repository, string pattern, string path, bool names, string projectRoot, IProcessRunner runner, TimeSpan? timeout = null)
    {
        if (string.IsNullOrWhiteSpace(repository) || repository.Length > 200 ||
            string.IsNullOrWhiteSpace(pattern) || pattern.Length > 200 || path.Length > 500)
            return "Provide a registered repository and fixed text pattern of at most 200 characters.";

        string script = Path.Combine(projectRoot, "scripts", "workspace-search.py");
        if (!File.Exists(script)) script = Path.Combine(projectRoot, ".claude", "scripts", "workspace-search.py");
        if (!File.Exists(script)) return "Workspace search unavailable: set CLAUDE_PROJECT_DIR to the NoMercy workspace root.";

        List<string> args = ["--repo", repository, "--pattern", pattern, "--max-results", "15", "--timeout", "10"];
        if (names) args.Add("--names");
        if (!string.IsNullOrWhiteSpace(path)) { args.Add("--path"); args.Add(path); }

        return HandoverProcess.RunPython(runner, script, args, projectRoot, "Workspace search", timeout ?? DefaultTimeout);
    }
}
