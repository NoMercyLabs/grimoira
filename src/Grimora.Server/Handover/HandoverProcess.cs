using Grimora.Brain.Data;

namespace Grimora.Server.Handover;

/// <summary>
/// Shared plumbing for the handover tools that shell out to a subprocess
/// (<see cref="WorkspaceSearchTool"/>, <see cref="WorkspaceCapabilitiesTool"/>, <see cref="IdPTokenTool"/>).
/// Ported from mcp.cs's <c>RunWorkspacePython</c> (mcp.cs:338), including its timeout/kill handling
/// (mcp.cs:356) — <see cref="RunWithTimeout"/> bounds the wait itself with a background task, so the
/// timeout applies even against a runner (fake or real) that never returns, not only a cooperating one.
/// The live tool in mcp.cs keeps its own copy of this behaviour until phase 3 retires it (RESTRUCTURE.md
/// section 0, "old and new code run side by side"). Internal: not part of the tool contract, only a helper
/// the tools in this folder share.
/// </summary>
internal static class HandoverProcess
{
    internal static string RunPython(IProcessRunner runner, string script, IReadOnlyList<string> scriptArguments, string workingDirectory, string operation, TimeSpan timeout)
    {
        string python = Environment.GetEnvironmentVariable("GRIMORA_PYTHON")
            ?? (OperatingSystem.IsWindows() ? "python" : "python3");
        List<string> args = [script, .. scriptArguments];
        try
        {
            (string stdout, string stderr, int exitCode) = RunWithTimeout(runner, python, args, workingDirectory, timeout);
            _ = stderr; // Drain but never surface raw subprocess diagnostics, as mcp.cs did.
            string output = stdout;
            if (output.Length > 12000) output = output[..12000] + "\nOutput truncated; coverage incomplete.";
            return exitCode == 0
                ? output
                : $"{operation} incomplete (exit {exitCode}). Narrow the task or inspect the named repository.\n{output}";
        }
        catch (TimeoutException)
        {
            return $"{operation} timed out; coverage is incomplete. Narrow the task and retry.";
        }
        catch (Exception)
        {
            return $"Could not start {operation.ToLowerInvariant()}. Check Python availability or configure GRIMORA_PYTHON.";
        }
    }

    /// <summary>Runs <paramref name="runner"/> on a background task and waits at most <paramref name="timeout"/>
    /// for it, throwing <see cref="TimeoutException"/> if it does not return in time — a backstop that holds
    /// even when the runner itself (a fake in tests, or a real process that ignores signals) never returns.</summary>
    internal static (string Stdout, string Stderr, int ExitCode) RunWithTimeout(IProcessRunner runner, string fileName, IReadOnlyList<string> args, string? workingDirectory, TimeSpan timeout)
    {
        Task<(string Stdout, string Stderr, int ExitCode)> task = Task.Run(() => runner.Run(fileName, args, workingDirectory, timeout));
        if (!task.Wait(timeout)) throw new TimeoutException($"{fileName} did not complete within {timeout}.");
        return task.Result;
    }
}
