namespace Aitm.Server.Data;

/// <summary>
/// Finds the files that ship in the plugin root (RESTRUCTURE.md slice 32a). An installed plugin runs the server
/// from <c>${CLAUDE_PLUGIN_DATA}/current/bin-server</c>, outside the plugin root, so the folders above the
/// server's own no longer hold them. The SessionStart step passes the plugin root as <c>AITM_PLUGIN_ROOT</c>
/// to the hook, and the server started by that hook inherits it. A checkout build has no such variable and
/// keeps finding the files next to or above its own folder, as before.
/// </summary>
public static class PluginFileLocator
{
    public const string PluginRootVariable = "AITM_PLUGIN_ROOT";

    /// <summary>The plugin root this server was started with, or null.</summary>
    public static string? PluginRoot() =>
        Environment.GetEnvironmentVariable(PluginRootVariable) is { Length: > 0 } root ? root : null;

    /// <summary>
    /// A script such as idp-impersonate.mjs: <paramref name="home"/> (AITM_HOME) when set, then the plugin
    /// root, then up to six folders up from <paramref name="baseDirectory"/>. Null when none has it.
    /// </summary>
    public static string? FindEngine(string fileName, string? home, string? pluginRoot, string baseDirectory)
    {
        if (!string.IsNullOrEmpty(home))
        {
            string p = Path.Combine(home, fileName);
            return File.Exists(p) ? p : null;
        }
        if (!string.IsNullOrEmpty(pluginRoot))
        {
            string p = Path.Combine(pluginRoot, fileName);
            if (File.Exists(p)) return p;
        }
        string? dir = baseDirectory;
        for (int i = 0; i < 6 && dir != null; i++)
        {
            string p = Path.Combine(dir, fileName);
            if (File.Exists(p)) return p;
            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        }
        return null;
    }

    /// <summary>The default spine seed: <c>&lt;plugin root&gt;/seeds/spine.json</c>, else beside the checkout build.</summary>
    public static string SeedPath(string? pluginRoot, string baseDirectory) =>
        !string.IsNullOrEmpty(pluginRoot)
            ? Path.Combine(pluginRoot, "seeds", "spine.json")
            : Path.Combine(baseDirectory, "..", "seeds", "spine.json");

    /// <summary><see cref="SeedPath(string?, string)"/> for this server process.</summary>
    public static string SeedPath() => SeedPath(PluginRoot(), AppContext.BaseDirectory);
}
