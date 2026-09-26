using System.Diagnostics;

namespace Aitm.Store.Tests.Support;

/// <summary>
/// Builds a real-shaped v3 store the same way a user's does: by running the actual
/// <c>bin-cli-old/aitm.dll</c> — the kept aitm.cs build — against a fresh <c>test-*</c> instance. This is
/// the oracle for slice 3c's "a copy of a real-shaped v3 store ... create it with today's aitm.cs
/// `init`" — the real CLI, not a reimplementation of what it creates.
///
/// The instance still lands under the real <c>~/.aitm/</c> (Windows' <c>Environment.GetFolderPath
/// (UserProfile)</c> resolves through the OS user-profile API, not the <c>USERPROFILE</c> env var, so a
/// child process cannot be redirected to a fake home). <c>verify.ps1</c>'s own selftest run already
/// uses this same "real folder, throwaway <c>test-*</c> name, delete it afterwards" pattern for the
/// same reason. <see cref="Dispose"/> removes the instance folder; the guard below refuses to touch
/// anything not shaped like a throwaway test instance, so this can never reach a real store.
/// </summary>
internal sealed class V3StoreFixture : IDisposable
{
    public string DbPath { get; }
    private readonly string _instanceDir;

    private V3StoreFixture(string dbPath, string instanceDir)
    {
        DbPath = dbPath;
        _instanceDir = instanceDir;
    }

    /// <summary>Runs `init` plus a couple of `add` calls on a fresh throwaway <c>test-*</c> instance and
    /// returns a fixture whose <see cref="DbPath"/> points at the resulting <c>aitm.db</c>. Dispose the
    /// fixture to remove the throwaway instance folder.</summary>
    public static V3StoreFixture Create()
    {
        string instance = $"test-schemarunner-{Guid.NewGuid():N}";
        string dll = FindAitmDll();

        RunAitm(dll, $"init --instance {instance}");
        RunAitm(dll, $"add --instance {instance} --term fixture-fact-one --value one --category manual");
        RunAitm(dll, $"add --instance {instance} --term fixture-fact-two --value two --category manual");

        string instanceDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".aitm", instance);
        string dbPath = Path.Combine(instanceDir, "aitm.db");
        if (!File.Exists(dbPath))
            throw new InvalidOperationException($"fixture store was not created at {dbPath}");
        return new V3StoreFixture(dbPath, instanceDir);
    }

    public void Dispose()
    {
        string instanceName = Path.GetFileName(_instanceDir);
        if (!instanceName.StartsWith("test-", StringComparison.Ordinal))
            throw new InvalidOperationException($"refusing to delete '{_instanceDir}': not a test-* instance");
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_instanceDir)) Directory.Delete(_instanceDir, recursive: true);
    }

    // Through `dotnet <dll>`, so it runs the same on Windows and on the Linux CI runner.
    private static void RunAitm(string dll, string arguments)
    {
        ProcessStartInfo psi = new("dotnet", $"\"{dll}\" {arguments}")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        using Process process = Process.Start(psi) ?? throw new InvalidOperationException($"could not start dotnet {dll}");
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"'dotnet {dll} {arguments}' exited {process.ExitCode}\nSTDOUT:\n{stdout}\nSTDERR:\n{stderr}");
    }

    /// <summary>Walks up from the test assembly's output directory to find the repo-root
    /// <c>bin-cli-old/aitm.dll</c> (the aitm.cs build) that build-cli.ps1 produces.</summary>
    private static string FindAitmDll()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "bin-cli-old", "aitm.dll");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new InvalidOperationException(
            $"bin-cli-old/aitm.dll not found above {AppContext.BaseDirectory} — run build-cli.ps1 first");
    }
}
