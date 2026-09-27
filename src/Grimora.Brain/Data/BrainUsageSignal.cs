using System.Globalization;
using Grimora.Store.Data;
using Microsoft.Data.Sqlite;

namespace Grimora.Brain.Data;

/// <summary>
/// Brain's own <see cref="IUsageSignal"/> (RESTRUCTURE.md section 1: "Store defines an IUsageSignal. A
/// channel calls it. Brain implements it."). Identical SQL to <see cref="Grimora.Store.Data.UsageSignal"/> —
/// join the channel's <c>ref</c> row (a table Brain, not Store, owns) to find the linked node, then bump
/// <c>usage.hits</c>/<c>last_used</c> for it — but living in Grimora.Brain now that Brain exists as its own
/// project, so a channel (Facts' <c>QueryTool</c>, Memory's <c>MemTool</c>) reinforces the brain graph
/// through the interface without ever referencing Brain directly. Copied verbatim from mcp.cs's
/// <c>ReinforceChannel</c> (mcp.cs:190), same oracle <see cref="Grimora.Store.Data.UsageSignal"/> was copied
/// from. A missing signal (brain tables not created on this store) is swallowed exactly as it is today —
/// never a reason to fail the read that triggered it.
/// </summary>
public sealed class BrainUsageSignal : IUsageSignal
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
