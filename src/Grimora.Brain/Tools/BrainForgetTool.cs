using Grimora.Store.Data;
using Grimora.Store.Tools;
using Microsoft.Data.Sqlite;

namespace Grimora.Brain.Tools;

/// <summary>
/// Deliberate removal: retire a node confirmed wrong/obsolete along with its live edges and slots, so the
/// audit stays clean (no dead-reference triples left behind). The "this is wrong, kill it" half of the
/// freshness loop. Copied verbatim from grimora.cs's <c>BrainForget</c> (grimora.cs:1758-1770). RESTRUCTURE.md
/// section 5 says a delete/bulk-change verb backs up first (design checklist; as forget-project).
/// </summary>
public sealed class BrainForgetTool : ITool
{
    public string Name => "brain forget";
    public string CliVerb => "brain forget";
    public string? McpName => null;
    public string Help => "brain forget <node-key>   retire a node and its live edges/slots";

    public string Execute(SqliteConnection connection, string root, string k)
    {
        new BackupTool().Execute(connection, root, null);

        using (SqliteCommand count = connection.CreateCommand())
        {
            count.CommandText = "SELECT count(*) FROM node_now WHERE k=$k";
            count.Parameters.AddWithValue("$k", k);
            if ((long)(count.ExecuteScalar() ?? 0L) == 0)
                return $"no live node '{k}'.";
        }

        string now = DateTime.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture);
        Run(connection, "UPDATE triple SET valid_to=$now WHERE valid_to IS NULL AND (s=$k OR (o=$k AND o_is_literal=0))", ("$now", now), ("$k", k));
        Run(connection, "UPDATE slot SET valid_to=$now WHERE valid_to IS NULL AND frame_k=$k", ("$now", now), ("$k", k));
        Run(connection, "DELETE FROM ref WHERE node_k=$k", ("$k", k));
        Run(connection, "UPDATE node SET valid_to=$now WHERE k=$k AND valid_to IS NULL", ("$now", now), ("$k", k));
        MutationLog.Append(connection, "node", k, "forget", k, null, "retired");

        return $"forgot {k} (node + its live edges/slots retired).";
    }

    private static void Run(SqliteConnection connection, string sql, params (string name, object? value)[] parameters)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        foreach ((string name, object? value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        command.ExecuteNonQuery();
    }
}
