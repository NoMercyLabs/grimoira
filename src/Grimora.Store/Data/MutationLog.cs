using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Grimora.Store.Data;

/// <summary>
/// The single append-only write path (RESTRUCTURE.md section 1, mutation log): every knowledge-base
/// change lands here with its before/after snapshot, so the cold log is the complete trace. Copied
/// verbatim from grimora.cs's <c>LogMutation</c> (grimora.cs:633). The caller owns the transaction.
/// </summary>
public static class MutationLog
{
    public static void Append(SqliteConnection connection, string kind, string key, string op, string? before, string? after, string why)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "INSERT INTO mutations(ts,op,kind,k,before,after,why) VALUES($ts,$op,$kind,$k,$b,$af,$why)";
        command.Parameters.AddWithValue("$ts", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$op", op);
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$k", key);
        command.Parameters.AddWithValue("$b", (object?)before ?? DBNull.Value);
        command.Parameters.AddWithValue("$af", (object?)after ?? DBNull.Value);
        command.Parameters.AddWithValue("$why", why);
        command.ExecuteNonQuery();
    }
}
