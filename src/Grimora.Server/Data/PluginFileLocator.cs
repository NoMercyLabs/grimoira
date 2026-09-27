namespace Grimora.Server.Data;

/// <summary>
/// Finds the files that ship in the plugin root (RESTRUCTURE.md slice 32a). An installed plugin runs the server
/// from <c>${CLAUDE_PLUGIN_DATA}/current/bin-server</c>, outside the plugin root, so the folders above the
/// server's own no longer hold them. The plugin root comes from, in order: <c>GRIMORA_PLUGIN_ROOT</c> (the
/// SessionStart step passes it to the hook, and a server that hook starts inherits it), then
/// <c>&lt;data&gt;/plugin-root.txt</c> (SessionStart writes it every session, for a
/// server started by the logon task or a thin client). A root that no longer exists (an old plugin version
/// folder) is skipped. A checkout build has neither and keeps finding the files next to or above its own
/// folder, as before.
/// </summary>
public static class PluginFileLocator
{
    public const string PluginRootVariable = "GRIMORA_PLUGIN_ROOT";

    public const string PluginRootFile = "plugin-root.txt";

    /// <summary>The plugin root this server was started with, or null.</summary>
    public static string? PluginRoot() =>
        Environment.GetEnvironmentVariable(PluginRootVariable) is { Length: > 0 } root ? root : null;

    /// <summary>The default spine seed: <c>&lt;plugin root&gt;/seeds/spine.json</c>, else beside the checkout build.</summary>
    public static string SeedPath(string? pluginRoot, string baseDirectory) =>
        ResolvePluginRoot(pluginRoot, baseDirectory) is { } root
            ? Path.Combine(root, "seeds", "spine.json")
            : Path.Combine(baseDirectory, "..", "seeds", "spine.json");

    /// <summary>
    /// The first plugin root that still exists: <paramref name="variableRoot"/>, then the one recorded in
    /// <c>&lt;data&gt;/plugin-root.txt</c>, where the server runs from <c>&lt;data&gt;/builds/&lt;id&gt;/bin-server</c> or
    /// <c>&lt;data&gt;/current/bin-server</c>. Null when neither does.
    /// </summary>
    public static string? ResolvePluginRoot(string? variableRoot, string baseDirectory)
    {
        if (!string.IsNullOrEmpty(variableRoot) && Directory.Exists(variableRoot)) return variableRoot;
        try
        {
            // Through the junction the data folder is two up (current/bin-server); by its real path, three up
            // (builds/<id>/bin-server).
            string[] candidates =
            [
                Path.Combine(baseDirectory, "..", "..", PluginRootFile),
                Path.Combine(baseDirectory, "..", "..", "..", PluginRootFile),
            ];
            string? file = candidates.FirstOrDefault(File.Exists);
            if (file is null) return null;
            string recorded = File.ReadAllText(file).Trim();
            return recorded.Length > 0 && Directory.Exists(recorded) ? recorded : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary><see cref="SeedPath(string?, string)"/> for this server process.</summary>
    public static string SeedPath() => SeedPath(PluginRoot(), AppContext.BaseDirectory);
}
