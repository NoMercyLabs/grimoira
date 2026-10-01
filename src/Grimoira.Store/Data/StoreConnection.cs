using Microsoft.Data.Sqlite;

namespace Grimoira.Store.Data;

/// <summary>
/// Opening a store: instance resolution and the pragmas every connection needs. Copied from grimoira.cs
/// (ResolveInstance at grimoira.cs:325, the pragma pair at grimoira.cs:27-35) so the new code observes the
/// same instance name and the same pragma state as the old CLI for the same inputs.
/// </summary>
public static class StoreConnection
{
    /// <summary>Resolves the instance name the same way grimoira.cs's ResolveInstance() does, reading the
    /// process environment and the current directory.</summary>
    public static string ResolveInstance() => ResolveInstance(
        Environment.GetEnvironmentVariable("GRIMOIRA_INSTANCE"),
        Environment.GetEnvironmentVariable("CLAUDE_PROJECT_DIR"),
        Directory.GetCurrentDirectory());

    /// <summary>The injectable form of <see cref="ResolveInstance()"/>, so a test can pin every input
    /// without touching the real process environment or the real current directory.</summary>
    public static string ResolveInstance(string? grimoiraInstanceEnv, string? claudeProjectDirEnv, string currentDirectory)
    {
        if (!string.IsNullOrWhiteSpace(grimoiraInstanceEnv)) return Slug(grimoiraInstanceEnv);
        string dir = string.IsNullOrWhiteSpace(claudeProjectDirEnv) ? currentDirectory : claudeProjectDirEnv;
        string name = Path.GetFileName(dir.TrimEnd('/', '\\'));
        return string.IsNullOrWhiteSpace(name) ? "default" : Slug(name);
    }

    /// <summary>Same slugging rule as grimoira.cs's Slug(): lowercase, letters/digits/-/_ only.</summary>
    public static string Slug(string text) =>
        new([.. text.ToLowerInvariant().Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_')]);

    /// <summary>Opens the sqlite file at <paramref name="dbPath"/> and applies the pragmas every
    /// Grimoira connection needs, exactly as grimoira.cs does before it touches the schema.</summary>
    public static SqliteConnection Open(string dbPath)
    {
        SqliteConnection connection = new($"Data Source={dbPath};Foreign Keys=True");
        connection.Open();
        ApplyPragmas(connection);
        return connection;
    }

    /// <summary>The pragma pair grimoira.cs applies on every open (grimoira.cs:27-35): a busy timeout so a
    /// momentary writer blocks the connection instead of killing it, then WAL so writers no longer
    /// starve readers. WAL is best-effort — a failure here means someone else is mid-write, which is
    /// not a reason to fail the open.</summary>
    public static void ApplyPragmas(SqliteConnection connection)
    {
        Exec(connection, "PRAGMA busy_timeout=30000");
        TryExec(connection, "PRAGMA journal_mode=WAL");
    }

    private static void Exec(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void TryExec(SqliteConnection connection, string sql)
    {
        try { Exec(connection, sql); }
        catch (SqliteException) { }
    }
}
