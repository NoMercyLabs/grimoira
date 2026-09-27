using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Grimora.Brain.Data;

/// <summary>
/// Auto-resolves an open gap once a write's text covers every one of its tokens — the fill half of
/// Store's gap log (<see cref="Grimora.Store.Data.GapLog"/> records the miss; this clears it). Copied
/// verbatim from grimora.cs's <c>ResolveGaps</c> (grimora.cs:427), called from every learn write-path (single
/// learn, learn-batch, and — once they move — flush and seed).
/// </summary>
public static class BrainGapResolver
{
    public static void Resolve(SqliteConnection connection, string learnedText)
    {
        string hay = learnedText.ToLowerInvariant();
        List<long> filled = [];
        using (SqliteCommand select = connection.CreateCommand())
        {
            select.CommandText = "SELECT id, query FROM gaps WHERE status='open' LIMIT 200";
            using SqliteDataReader reader = select.ExecuteReader();
            while (reader.Read())
            {
                string query = reader.GetString(1);
                if (query.Split(' ').All(token => hay.Contains(token, StringComparison.Ordinal)))
                    filled.Add(reader.GetInt64(0));
            }
        }
        foreach (long id in filled)
        {
            using SqliteCommand update = connection.CreateCommand();
            update.CommandText = "UPDATE gaps SET status='filled', last_ts=$ts WHERE id=$id";
            update.Parameters.AddWithValue("$ts", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
            update.Parameters.AddWithValue("$id", id);
            update.ExecuteNonQuery();
        }
    }
}
