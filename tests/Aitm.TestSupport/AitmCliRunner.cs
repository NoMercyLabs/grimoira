using System.Diagnostics;

namespace Aitm.TestSupport;

/// <summary>
/// Runs today's compiled <c>bin-cli/aitm.dll</c> against a throwaway <c>test-*</c> instance and returns
/// its stdout — the oracle every project's pinned-output tests run against. Previously duplicated once
/// per test project (<c>Aitm.Store.Tests</c>, then <c>Aitm.Facts.Tests</c>) because the class was
/// internal to its own assembly; a third copy for <c>Aitm.Memory.Tests</c> would have made three, so
/// this is the one shared place instead (Aitm.Layout.Tests' folder rules only govern src/*, not
/// tests/*, so nothing there blocks the move).
/// </summary>
public static class AitmCliRunner
{
    public static string NewTestInstance(string label) => $"test-{label}-{Guid.NewGuid():N}";

    public static string InstanceDbPath(string instance) => Path.Combine(InstanceDir(instance), "aitm.db");

    public static string InstanceDir(string instance) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".aitm", instance);

    public static void DeleteInstance(string instance)
    {
        if (!instance.StartsWith("test-", StringComparison.Ordinal))
            throw new InvalidOperationException($"refusing to delete '{instance}': not a test-* instance");
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        string dir = InstanceDir(instance);
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    /// <summary>Runs <c>aitm &lt;arguments&gt;</c> and returns (stdout, exit code). Never throws on a
    /// non-zero exit so a caller can pin an error path too.</summary>
    public static (string stdout, int exitCode) Run(string arguments)
    {
        string dll = FindAitmDll();
        ProcessStartInfo psi = new("dotnet", $"\"{dll}\" {arguments}")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            // The CLI prints "•" and "—" (query's fixture hits). Without this the redirected pipe
            // is read back with the OS codepage, mangling both into control characters.
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        using Process process = Process.Start(psi) ?? throw new InvalidOperationException($"could not start dotnet {dll}");
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0 && string.IsNullOrEmpty(stdout))
            throw new InvalidOperationException($"'dotnet {dll} {arguments}' exited {process.ExitCode}\nSTDOUT:\n{stdout}\nSTDERR:\n{stderr}");
        return (stdout, process.ExitCode);
    }

    private static string FindAitmDll()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "bin-cli", "aitm.dll");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new InvalidOperationException(
            $"bin-cli/aitm.dll not found above {AppContext.BaseDirectory} — run build-cli.ps1 first");
    }
}
