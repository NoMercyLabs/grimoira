using System.Text.RegularExpressions;

namespace Aitm.Hooks.Data;

/// <summary>
/// Where a hook's per-instance, per-session files live. Ported verbatim from brain-lib.mjs's
/// <c>resolveInstance</c> and <c>briefPath</c> (RESTRUCTURE.md slice 20): compact-brief.mjs writes here
/// and compact-restore.mjs reads the same path back, so the two must compute it identically.
/// </summary>
public static class HookPaths
{
    public static string ResolveInstance(string? cwd)
    {
        string proj = Environment.GetEnvironmentVariable("CLAUDE_PROJECT_DIR")
            ?? cwd
            ?? Directory.GetCurrentDirectory();
        string trimmed = proj.TrimEnd('\\', '/');
        string name = Path.GetFileName(trimmed).ToLowerInvariant();
        return Regex.Replace(name, "[^a-z0-9_-]", "");
    }

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
}
