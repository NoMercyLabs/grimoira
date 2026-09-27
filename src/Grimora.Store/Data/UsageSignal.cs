using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Grimora.Store.Data;

/// <summary>
/// Copied verbatim from mcp.cs's <c>ReinforceChannel</c> (mcp.cs:190): bumps <c>usage.hits</c> for every
/// brain node ref-bridged to the given channel/payload keys. On a store with no <c>ref</c>/<c>usage</c>
/// wiring yet (brain tables not created), a failing write is swallowed exactly as it is today — a
/// missing signal is never a reason to fail the read that triggered it.
/// </summary>
public sealed class UsageSignal : IUsageSignal
{
    public void Reinforce(SqliteConnection connection, string channel, IEnumerable<string> payloadKeys)
    {
        foreach (string payloadKey in payloadKeys.Where(s => s.Length > 0).Distinct())
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO usage(node_k,hits,last_used)
                SELECT node_k,1,$ts FROM ref WHERE channel=$c AND payload_k=$p
                ON CONFLICT(node_k) DO UPDATE SET hits=hits+1, last_used=$ts
                """;
            command.Parameters.AddWithValue("$c", channel);
            command.Parameters.AddWithValue("$p", payloadKey);
            command.Parameters.AddWithValue("$ts", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
            try { command.ExecuteNonQuery(); } catch (SqliteException) { }
        }
    }
}
