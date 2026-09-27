using System.Globalization;
using Grimora.Store.Tools;
using Microsoft.Data.Sqlite;

namespace Grimora.Brain.Tools;

/// <summary>
/// Freshness write-path: mark a node re-confirmed against current reality (the half of the staleness loop
/// <c>brain stale</c> surfaces). Confirmation is not the same as being surfaced, so it has its own
/// timestamp. CLI-only sub-verb (no MCP counterpart, RESTRUCTURE.md section 2.2). Copied verbatim from
/// grimora.cs's <c>BrainVerify</c> (grimora.cs:1774). No mutation-log entry today (the CLI oracle never wrote
/// one for a verify), so this tool doesn't add one either — a genuine move, not an improve.
/// </summary>
public sealed class BrainVerifyTool : ITool
{
    public string Name => "brain verify";
    public string CliVerb => "brain verify";
    public string? McpName => null;
    public string Help =>
        "brain verify <node-key>                mark a live node re-confirmed against current reality " +
        "(sets usage.verified_at); the freshness half of `brain stale`.";

    public string Execute(SqliteConnection connection, string key)
    {
        if (ScalarLong(connection, "SELECT count(*) FROM node_now WHERE k=$k", key) == 0)
            return $"no live node '{key}'.";

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "INSERT INTO usage(node_k,hits,last_used,verified_at) VALUES($k,0,$ts,$ts) ON CONFLICT(node_k) DO UPDATE SET verified_at=$ts";
        command.Parameters.AddWithValue("$k", key);
        command.Parameters.AddWithValue("$ts", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();

        return $"verified {key} (confirmed against current code).";
    }

    private static long ScalarLong(SqliteConnection connection, string sql, string key)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$k", key);
        return (long)(command.ExecuteScalar() ?? 0L);
    }
}
