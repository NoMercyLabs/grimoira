using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Grimora.Brain.Data;

/// <summary>
/// Reinforce surfaced brain nodes: bump <c>usage.hits</c> and recency. Guarded on live-node existence so
/// non-node first columns (slot names, project shorthands) never create junk usage rows — the automatic
/// half of "smarter with use". Copied verbatim from grimora.cs's <c>Reinforce</c> (grimora.cs:1414) and mcp.cs's
/// <c>Reinforce</c> (mcp.cs:641) — identical SQL in both, so <c>brain scope</c>, <c>brain common</c> and
/// <c>brain place</c> (CLI and MCP, 6 call sites) share this one copy.
/// </summary>
public static class BrainUsage
{
    public static void Reinforce(SqliteConnection connection, IEnumerable<string> keys)
    {
        foreach (string k in keys.Where(s => s.Length > 0).Distinct())
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO usage(node_k,hits,last_used) SELECT $k,1,$ts WHERE EXISTS(SELECT 1 FROM node_now WHERE k=$k)
                ON CONFLICT(node_k) DO UPDATE SET hits=hits+1, last_used=$ts
                """;
            command.Parameters.AddWithValue("$k", k);
            command.Parameters.AddWithValue("$ts", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
            command.ExecuteNonQuery();
        }
    }
}
