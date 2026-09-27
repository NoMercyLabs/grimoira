using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Grimora.Store.Data;

/// <summary>
/// Every refused/empty lookup is recorded so the store knows what it does NOT know (RESTRUCTURE.md
/// section 1, gap log); a later learn whose text covers a gap's tokens auto-resolves it. Copied
/// verbatim from grimora.cs's <c>LogGap</c> (grimora.cs:409). Tokenising the raw query stays with the caller
/// (Facts owns <c>Tokens</c>) — this only stores the already-normalised query.
/// </summary>
public static class GapLog
{
    public static void Record(SqliteConnection connection, string tool, string normalizedQuery)
    {
        if (normalizedQuery.Length < 3) return;
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO gaps(query,tool,misses,first_ts,last_ts) VALUES($q,$t,1,$ts,$ts)
            ON CONFLICT(query) DO UPDATE SET misses=misses+1, last_ts=$ts, status='open', tool=$t
            """;
        command.Parameters.AddWithValue("$q", normalizedQuery);
        command.Parameters.AddWithValue("$t", tool);
        command.Parameters.AddWithValue("$ts", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }
}
