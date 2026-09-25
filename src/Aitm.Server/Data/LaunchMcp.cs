using Aitm.Brain.Data;

namespace Aitm.Server.Data;

/// <summary>
/// The tested rules of launch-mcp.mjs: where the build lands, what <c>dotnet build</c> is run with,
/// and that a failed build never launches. As a plugin, bin/ is gitignored and never shipped, so a
/// fresh install has no prebuilt DLL; CLAUDE_PLUGIN_DATA (a per-plugin, update-safe directory Claude
/// Code provides) holds that first build when set, so a plugin update never clobbers it. Becomes the
/// server's version and restart rules (RESTRUCTURE.md slice 23). The shadow-copy launch step itself is
/// out of this slice's scope; only the build decision moves.
/// </summary>
public static class LaunchMcp
{
    public static string BinDir(string pluginDir, string? pluginDataDir) =>
        pluginDataDir is null ? Path.Combine(pluginDir, "bin") : Path.Combine(pluginDataDir, "bin");

    public static IReadOnlyList<string> BuildArgs(string sourceFile, string binDir) =>
        ["build", sourceFile, "-c", "Release", "-o", binDir];

    /// <summary>
    /// Builds the server when <see cref="BuildStamp.NeedsBuild"/> says it is stale. Returns
    /// <c>true</c> and writes the stamp on success; returns <c>false</c> without writing a stamp when
    /// the build fails, so the caller never launches against a missing or broken build.
    /// </summary>
    public static bool EnsureBuilt(string pluginDir, string? pluginDataDir, string sourceFile, IProcessRunner runner)
    {
        string binDir = BinDir(pluginDir, pluginDataDir);
        if (!BuildStamp.NeedsBuild(binDir, sourceFile)) return true;

        Directory.CreateDirectory(binDir);
        (_, _, int exitCode) = runner.Run("dotnet", BuildArgs(sourceFile, binDir));
        if (exitCode != 0) return false;

        BuildStamp.WriteStamp(binDir, sourceFile);
        return true;
    }
}
