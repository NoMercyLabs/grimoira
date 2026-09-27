namespace Grimora.Brain.Data;

/// <summary>
/// The one seam <c>BrainIndexOrgTool</c> spawns a process through — <c>gh repo list</c> and
/// <c>git remote get-url</c> in index-org.mjs's <c>reposOf</c>/<c>localClones</c> (index-org.mjs:31,45).
/// A test injects a fake so it never calls the real <c>gh</c> CLI or the network.
/// </summary>
public interface IProcessRunner
{
    /// <param name="timeout">When given, the runner waits at most this long and, for a real process,
    /// kills the whole tree and throws <see cref="TimeoutException"/> if it has not exited by then.</param>
    (string Stdout, string Stderr, int ExitCode) Run(string fileName, IReadOnlyList<string> args, string? workingDirectory = null, TimeSpan? timeout = null);
}

/// <summary>Real process runner — the default outside tests.</summary>
public sealed class ProcessRunner : IProcessRunner
{
    public (string Stdout, string Stderr, int ExitCode) Run(string fileName, IReadOnlyList<string> args, string? workingDirectory = null, TimeSpan? timeout = null)
    {
        System.Diagnostics.ProcessStartInfo psi = new(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            WorkingDirectory = workingDirectory ?? "",
        };
        foreach (string a in args) psi.ArgumentList.Add(a);
        using System.Diagnostics.Process process = System.Diagnostics.Process.Start(psi)
            ?? throw new InvalidOperationException($"could not start {fileName}");
        // Without an explicit redirect+close, the child inherits THIS process's own stdin. When the
        // caller is an MCP stdio server (workspace_capabilities/workspace_search, ported from mcp.cs's
        // RunWorkspacePython, which did the same close), that pipe never reaches EOF on its own — a
        // child that reads stdin to completion (e.g. workspace-search.py) then hangs until the caller's
        // timeout, instead of running and returning like the real script does.
        process.StandardInput.Close();
        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = process.StandardError.ReadToEndAsync();
        if (timeout is { } t)
        {
            if (!process.WaitForExit((int)t.TotalMilliseconds))
            {
                try { process.Kill(entireProcessTree: true); } catch (Exception) { /* best effort */ }
                throw new TimeoutException($"{fileName} did not exit within {t}.");
            }
        }
        else
        {
            process.WaitForExit();
        }
        return (stdoutTask.Result, stderrTask.Result, process.ExitCode);
    }
}
