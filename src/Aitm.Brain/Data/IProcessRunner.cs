namespace Aitm.Brain.Data;

/// <summary>
/// The one seam <c>BrainIndexOrgTool</c> spawns a process through — <c>gh repo list</c> and
/// <c>git remote get-url</c> in index-org.mjs's <c>reposOf</c>/<c>localClones</c> (index-org.mjs:31,45).
/// A test injects a fake so it never calls the real <c>gh</c> CLI or the network.
/// </summary>
public interface IProcessRunner
{
    (string Stdout, string Stderr, int ExitCode) Run(string fileName, IReadOnlyList<string> args, string? workingDirectory = null);
}

/// <summary>Real process runner — the default outside tests.</summary>
public sealed class ProcessRunner : IProcessRunner
{
    public (string Stdout, string Stderr, int ExitCode) Run(string fileName, IReadOnlyList<string> args, string? workingDirectory = null)
    {
        System.Diagnostics.ProcessStartInfo psi = new(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = workingDirectory ?? "",
        };
        foreach (string a in args) psi.ArgumentList.Add(a);
        using System.Diagnostics.Process process = System.Diagnostics.Process.Start(psi)
            ?? throw new InvalidOperationException($"could not start {fileName}");
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (stdout, stderr, process.ExitCode);
    }
}
