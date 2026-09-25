using Aitm.Brain.Data;

namespace Aitm.Server.Handover;

/// <summary>
/// Shared plumbing for the handover tools that shell out to a NoMercy workspace Python script
/// (<see cref="WorkspaceSearchTool"/>, <see cref="WorkspaceCapabilitiesTool"/>). Ported from mcp.cs's
/// <c>RunWorkspacePython</c> (mcp.cs:338), minus the async timeout/kill handling — <see cref="IProcessRunner"/>
/// is synchronous, and the live tool in mcp.cs keeps that behaviour until phase 3 retires it (RESTRUCTURE.md
/// section 0, "old and new code run side by side"). Internal: not part of the tool contract, only a helper
/// the two tools in this folder share.
/// </summary>
internal static class HandoverProcess
{
    internal static string RunPython(IProcessRunner runner, string script, IReadOnlyList<string> scriptArguments, string workingDirectory, string operation)
    {
        string python = Environment.GetEnvironmentVariable("AITM_PYTHON")
            ?? (OperatingSystem.IsWindows() ? "python" : "python3");
        List<string> args = new(scriptArguments.Count + 1) { script };
        args.AddRange(scriptArguments);
        try
        {
            (string stdout, string stderr, int exitCode) = runner.Run(python, args, workingDirectory);
            _ = stderr; // Drain but never surface raw subprocess diagnostics, as mcp.cs did.
            string output = stdout;
            if (output.Length > 12000) output = output[..12000] + "\nOutput truncated; coverage incomplete.";
            return exitCode == 0
                ? output
                : $"{operation} incomplete (exit {exitCode}). Narrow the task or inspect the named repository.\n{output}";
        }
        catch (Exception)
        {
            return $"Could not start {operation.ToLowerInvariant()}. Check Python availability or configure AITM_PYTHON.";
        }
    }
}
