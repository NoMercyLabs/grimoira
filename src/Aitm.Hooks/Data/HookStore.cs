using Microsoft.Data.Sqlite;

namespace Aitm.Hooks.Data;

/// <summary>
/// Opens a store the way a SessionEnd/PostToolUse hook must: a short lock-wait set BEFORE anything else
/// touches the connection. <see cref="Aitm.Store.Data.StoreConnection.Open"/> sets the store's usual 30s
/// timeout and then switches on WAL, so calling it while another connection holds a write lock (the exact
/// case these hooks must fail open on) makes that connection wait the full 30s. Microsoft.Data.Sqlite's
/// own busy retry loop is driven by the connection string's <c>Default Timeout</c> keyword (seconds), not
/// by a <c>PRAGMA busy_timeout</c> statement run after Open — a pragma has no effect on it, confirmed by
/// running both side by side. RESTRUCTURE.md slice 21 ("Hooks, part 2"): a hook must never hold up
/// session exit or an edit waiting on a lock.
/// </summary>
internal static class HookStore
{
    private const int DefaultTimeoutSeconds = 3;

    public static SqliteConnection Open(string dbPath)
    {
        SqliteConnection connection = new($"Data Source={dbPath};Foreign Keys=True;Default Timeout={DefaultTimeoutSeconds}");
        connection.Open();
        try
        {
            using SqliteCommand wal = connection.CreateCommand();
            wal.CommandText = "PRAGMA journal_mode=WAL";
            wal.ExecuteNonQuery();
        }
        catch (SqliteException)
        {
            // best effort — a held lock means someone else is mid-write, not a reason to fail the open
        }
        return connection;
    }
}
