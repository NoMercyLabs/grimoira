using System.Text.RegularExpressions;

namespace Aitm.Hooks.Data;

/// <summary>
/// Where a hook's per-instance, per-session files live. Ported verbatim from brain-lib.mjs's
/// <c>resolveInstance</c> and <c>briefPath</c> (RESTRUCTURE.md slice 20): compact-brief.mjs writes here
/// and compact-restore.mjs reads the same path back, so the two must compute it identically.
/// </summary>
public static partial class HookPaths
{
    public static string ResolveInstance(string? cwd) => ResolveInstance(cwd, projectDir: null);

    /// <summary>The instance of <see cref="ProjectDir"/>.</summary>
    public static string ResolveInstance(string? cwd, string? projectDir)
    {
        string trimmed = ProjectDir(cwd, projectDir).TrimEnd('\\', '/');
        string name = Path.GetFileName(trimmed).ToLowerInvariant();
        return NonSlugCharacters().Replace(name, "");
    }

    /// <summary>
    /// The project a hook works for: <paramref name="projectDir"/> when the caller already resolved it, else
    /// CLAUDE_PROJECT_DIR, else the payload's cwd, else the current directory. A command hook (the CLI) passes
    /// null: Claude Code gives it CLAUDE_PROJECT_DIR. The shared server passes the request's project, because
    /// its own env is the env of whichever session started it.
    /// </summary>
    public static string ProjectDir(string? cwd, string? projectDir) =>
        projectDir
        ?? Environment.GetEnvironmentVariable("CLAUDE_PROJECT_DIR")
        ?? cwd
        ?? Directory.GetCurrentDirectory();

    public static string BriefPath(string instance, string? sessionId)
    {
        string sid = string.IsNullOrEmpty(sessionId) ? "x" : sessionId;
        if (sid.Length > 64) sid = sid[..64];
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".aitm", instance, "compact", $"{sid}.md");
    }

    /// <summary>Where an instance's own directory lives, same layout as aitm.cs and the other hooks
    /// (RESTRUCTURE.md slice 21): <c>~/.aitm/&lt;instance&gt;</c>.</summary>
    public static string InstanceDir(string instance) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".aitm", instance);

    /// <summary>Where an instance's store lives. The SessionEnd/PostToolUse hooks check this exists
    /// before opening a connection, same as the .mjs files' <c>existsSync(... 'aitm.db')</c> guard.</summary>
    public static string DbPath(string instance) => Path.Combine(InstanceDir(instance), "aitm.db");

    [GeneratedRegex("[^a-z0-9_-]")]
    private static partial Regex NonSlugCharacters();
}
