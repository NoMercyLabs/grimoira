using System.Diagnostics;

namespace Grimora.Cli.Tests;

/// <summary>Runs the grimora.dll built beside this test assembly as a separate process, with Grimora_DATA_DIR set.</summary>
internal static class BuiltCli
{
    internal static (string Stdout, string Stderr, int Exit) Run(string[] args, string dataDir, IDictionary<string, string>? extraEnv = null)
    {
        string dll = Path.Combine(AppContext.BaseDirectory, "grimora.dll");
        ProcessStartInfo psi = new("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(dll);
        foreach (string a in args) psi.ArgumentList.Add(a);
        psi.Environment["Grimora_DATA_DIR"] = dataDir;
        if (extraEnv is not null) foreach ((string k, string v) in extraEnv) psi.Environment[k] = v;
        using Process p = Process.Start(psi)!;
        p.StandardInput.Write("{}");
        p.StandardInput.Close();
        Task<string> stdout = p.StandardOutput.ReadToEndAsync();
        Task<string> stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(30000))
        {
            p.Kill(entireProcessTree: true);
            throw new TimeoutException("grimora did not exit within 30 s");
        }
        return (stdout.Result, stderr.Result, p.ExitCode);
    }
}
