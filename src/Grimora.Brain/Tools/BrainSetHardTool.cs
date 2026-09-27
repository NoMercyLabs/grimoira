using Grimora.Brain.Data;
using Grimora.Store.Tools;
using Microsoft.Data.Sqlite;

namespace Grimora.Brain.Tools;

/// <summary>
/// Correct a node's always-on flag through proper supersession (keeps history; resyncs FTS), rather than a
/// raw UPDATE. CLI-only sub-verb (no MCP counterpart, RESTRUCTURE.md section 2.2). Copied verbatim from
/// grimora.cs's <c>BrainSetHard</c> (grimora.cs:1730); the write itself now goes through
/// <see cref="BrainWriters.AddNode"/> instead of a private local copy of the same supersede logic.
/// </summary>
public sealed class BrainSetHardTool : ITool
{
    public string Name => "brain set-hard";
    public string CliVerb => "brain set-hard";
    public string? McpName => null;
    public string Help =>
        "brain set-hard <node-key> <0|1>        flip a live node's hard flag (always-on core membership) " +
        "without a raw UPDATE — keeps history, resyncs FTS.";

    public string Execute(SqliteConnection connection, string key, bool hard)
    {
        string? row = ScalarText(connection, key);
        if (row is null) return $"no live node '{key}'.";

        string[] fields = row.Split('\x1f');
        BeginTransaction(connection);
        BrainWriters.AddNode(connection, key, fields[0], fields[1], fields[2], fields[3], hard, "set-hard");
        CommitTransaction(connection);

        return $"{key} hard={(hard ? 1 : 0)}.";
    }

    private static string? ScalarText(SqliteConnection connection, string key)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT kind||char(31)||label||char(31)||gloss||char(31)||COALESCE(scheme,'') FROM node_now WHERE k=$k";
        command.Parameters.AddWithValue("$k", key);
        return command.ExecuteScalar() as string;
    }

    private static void BeginTransaction(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "BEGIN";
        command.ExecuteNonQuery();
    }

    private static void CommitTransaction(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "COMMIT";
        command.ExecuteNonQuery();
    }
}
