namespace Grimora.Hooks.Data;

/// <summary>
/// Where a hook's per-instance, per-session files live. Ported verbatim from brain-lib.mjs's
/// <c>resolveInstance</c> and <c>briefPath</c> (RESTRUCTURE.md slice 20): compact-brief.mjs writes here
/// and compact-restore.mjs reads the same path back, so the two must compute it identically.
///
/// Data-dir resolution here duplicates (not references — this file is also linked verbatim into
/// Grimora.Cli, which per CliReferencesNothingInGrimora may not take a ProjectReference on Grimora.Store or
/// anything else in Grimora) <see cref="Grimora.Store.Data.StoreConnection.ResolveDataDir()"/>: honour
/// <c>GRIMORA_DATA_DIR</c>, same as every other entry point (the CLI's ServerAddress/ServerAutoStart, the
/// server's own data dir), instead of always hardcoding <c>~/.grimora</c>.
///
/// Instance resolution here deliberately does NOT read <c>GRIMORA_INSTANCE</c> from the process
/// environment, even though the CLI's ServerAddress/ServerAutoStart and the MCP path do: several handlers
/// that call this (SessionEnd's, PostToolUse's, Stop's) run inside the ONE shared Grimora.Server process
/// serving every project's requests, not inside a per-session CLI process — reading env there would read
/// whichever session's env the shared server happened to inherit at its own startup, not the requesting
/// session's, and silently misroute that request's writes to a wrong or stale instance (exactly the
/// per-request-state-in-process-env mistake <see cref="Grimora.Server.Data.RequestProjectResolver"/>'s own
/// doc comment already calls out: "It never reads the server process's own environment"). A caller that
/// resolved its own cwd/projectDir before calling in (the CLI's local PreCompact/UserPromptSubmit handlers,
/// the server's RequestProjectResolver for everything forwarded over HTTP) already has the right value; an
/// env-based override belongs at THAT layer, not duplicated unsafely here.
/// </summary>
public static class HookPaths
{
    public static string ResolveInstance(string? cwd) => ResolveInstance(cwd, projectDir: null);

    public static string ResolveInstance(string? cwd, string? projectDir)
    {
        string trimmed = ProjectDir(cwd, projectDir).TrimEnd('\\', '/');
        string name = Path.GetFileName(trimmed);
        string slug = Slug(name);
        return string.IsNullOrWhiteSpace(slug) ? "default" : slug;
    }

    /// <summary>Same slugging rule as <see cref="Grimora.Store.Data.StoreConnection.Slug"/>: lowercase,
    /// letters/digits/-/_ only.</summary>
    private static string Slug(string text) =>
        new([.. text.ToLowerInvariant().Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_')]);

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
        return Path.Combine(InstanceDir(instance), "compact", $"{sid}.md");
    }

    /// <summary>Where the verbatim compaction ledger lives, next to <see cref="BriefPath"/>: the brief is a
    /// budget-limited summary of what the user said since the last compaction, the ledger is the full record
    /// (every user, mid-turn, peer and assistant entry) a brief that hit its budget points back to.</summary>
    public static string LedgerPath(string instance, string? sessionId)
    {
        string sid = string.IsNullOrEmpty(sessionId) ? "x" : sessionId;
        if (sid.Length > 64) sid = sid[..64];
        return Path.Combine(InstanceDir(instance), "compact", $"{sid}.ledger.md");
    }

    /// <summary>Where an instance's own directory lives, same layout as grimora.cs and the other hosts
    /// (RESTRUCTURE.md slice 21): <c>&lt;data dir&gt;/&lt;instance&gt;</c>, where the data dir honours
    /// <c>GRIMORA_DATA_DIR</c> the same way the CLI and server do, instead of always hardcoding
    /// <c>~/.grimora</c>.</summary>
    public static string InstanceDir(string instance) => Path.Combine(ResolveDataDir(), instance);

    /// <summary>The data directory ("realm"): <c>GRIMORA_DATA_DIR</c>, else <c>~/.grimora</c>. An empty
    /// value counts as unset. Duplicates <see cref="Grimora.Store.Data.StoreConnection.ResolveDataDir()"/>
    /// for the same linked-into-Cli reason as <see cref="Slug"/> above.</summary>
    private static string ResolveDataDir()
    {
        string? configured = Environment.GetEnvironmentVariable("GRIMORA_DATA_DIR");
        if (!string.IsNullOrEmpty(configured)) return configured;
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".grimora");
    }

    /// <summary>Where an instance's store lives. The SessionEnd/PostToolUse hooks check this exists
    /// before opening a connection, same as the .mjs files' <c>existsSync(... 'grimora.db')</c> guard.</summary>
    public static string DbPath(string instance) => Path.Combine(InstanceDir(instance), "grimora.db");
}
