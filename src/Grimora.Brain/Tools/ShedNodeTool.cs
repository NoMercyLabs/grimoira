using Grimora.Store.Tools;
using Microsoft.Data.Sqlite;

namespace Grimora.Brain.Tools;

/// <summary>
/// Same gap forget-project had: the graph could gain a node but never lose one, so anything indexed by
/// mistake or moved out of scope stayed live forever. Retires on the timeline rather than deleting,
/// because "indexed once, now out of scope" is a different fact from "never existed". Copied verbatim
/// from grimora.cs's <c>case "shed-node"</c> (grimora.cs:145-156). RESTRUCTURE.md section 5 says a delete/
/// bulk-change verb backs up first (design checklist; as forget-project).
/// </summary>
public sealed class ShedNodeTool : ITool
{
    public string Name => "shed-node";
    public string CliVerb => "shed-node";
    public string? McpName => null;
    public string Help => "shed-node --key <k>   retire a node and its links (timeline, not delete)";

    public string Execute(SqliteConnection connection, string root, string key)
    {
        new BackupTool().Execute(connection, root, null);

        using (SqliteCommand begin = connection.CreateCommand()) { begin.CommandText = "BEGIN"; begin.ExecuteNonQuery(); }
        Run(connection, "UPDATE node SET valid_to = strftime('%Y-%m-%dT%H:%M:%fZ','now') WHERE k=$k AND valid_to IS NULL", ("$k", key));
        Run(connection, @"UPDATE triple SET valid_to = strftime('%Y-%m-%dT%H:%M:%fZ','now')
              WHERE valid_to IS NULL AND o_is_literal = 0 AND (s=$k OR o=$k)", ("$k", key));
        using (SqliteCommand commit = connection.CreateCommand()) { commit.CommandText = "COMMIT"; commit.ExecuteNonQuery(); }
        using (SqliteCommand rebuild = connection.CreateCommand()) { rebuild.CommandText = "INSERT INTO node_fts(node_fts) VALUES('rebuild')"; rebuild.ExecuteNonQuery(); }

        return $"retired node '{key}' and its links.";
    }

    private static void Run(SqliteConnection connection, string sql, params (string name, object? value)[] parameters)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        foreach ((string name, object? value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        command.ExecuteNonQuery();
    }
}
