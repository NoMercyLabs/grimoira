using Aitm.Store.Data;
using Aitm.Store.Tools;
using Microsoft.Data.Sqlite;

namespace Aitm.Brain.Tools;

/// <summary>
/// Dedup: fold a duplicate node into the canonical one. Re-points triples (subject + link object), slots,
/// refs and usage with collision-safe guards (a move that would duplicate a live edge/slot is dropped
/// instead), sums usage counts, then retires the source via supersession. FTS stays correct because reads
/// join node_now. Copied verbatim from aitm.cs's <c>BrainMerge</c> (aitm.cs:1806-1834). RESTRUCTURE.md
/// section 5 says a delete/bulk-change verb backs up first (design checklist; as forget-project).
/// </summary>
public sealed class BrainMergeTool : ITool
{
    public string Name => "brain merge";
    public string CliVerb => "brain merge";
    public string? McpName => null;
    public string Help => "brain merge <from-key> <into-key>   fold a duplicate node into the canonical one";

    public string Execute(SqliteConnection connection, string root, string from, string to)
    {
        if (from == to) return "nothing to merge (from == into).";

        if (ScalarLong(connection, "SELECT count(*) FROM node_now WHERE k=$k", ("$k", to)) == 0)
            return $"target '{to}' is not a live node.";
        if (ScalarLong(connection, "SELECT count(*) FROM node_now WHERE k=$k", ("$k", from)) == 0)
            return $"source '{from}' is not a live node.";

        new BackupTool().Execute(connection, root, null);

        string now = DateTime.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture);
        Run(connection, "UPDATE triple SET s=$to WHERE s=$from AND valid_to IS NULL AND NOT EXISTS (SELECT 1 FROM triple t2 WHERE t2.valid_to IS NULL AND t2.s=$to AND t2.p=triple.p AND t2.o=triple.o)", ("$to", to), ("$from", from));
        Run(connection, "UPDATE triple SET valid_to=$now WHERE s=$from AND valid_to IS NULL", ("$now", now), ("$from", from));
        Run(connection, "UPDATE triple SET o=$to WHERE o=$from AND o_is_literal=0 AND valid_to IS NULL AND NOT EXISTS (SELECT 1 FROM triple t2 WHERE t2.valid_to IS NULL AND t2.s=triple.s AND t2.p=triple.p AND t2.o=$to)", ("$to", to), ("$from", from));
        Run(connection, "UPDATE triple SET valid_to=$now WHERE o=$from AND o_is_literal=0 AND valid_to IS NULL", ("$now", now), ("$from", from));
        Run(connection, "UPDATE slot SET frame_k=$to WHERE frame_k=$from AND valid_to IS NULL AND NOT EXISTS (SELECT 1 FROM slot s2 WHERE s2.valid_to IS NULL AND s2.frame_k=$to AND s2.name=slot.name)", ("$to", to), ("$from", from));
        Run(connection, "UPDATE slot SET valid_to=$now WHERE frame_k=$from AND valid_to IS NULL", ("$now", now), ("$from", from));
        Run(connection, "UPDATE OR IGNORE ref SET node_k=$to WHERE node_k=$from", ("$to", to), ("$from", from));
        Run(connection, "DELETE FROM ref WHERE node_k=$from", ("$from", from));
        Run(connection, "UPDATE usage SET hits=hits+COALESCE((SELECT hits FROM usage WHERE node_k=$from),0) WHERE node_k=$to", ("$to", to), ("$from", from));
        Run(connection, "UPDATE OR IGNORE usage SET node_k=$to WHERE node_k=$from", ("$to", to), ("$from", from));
        Run(connection, "DELETE FROM usage WHERE node_k=$from", ("$from", from));
        Run(connection, "UPDATE node SET valid_to=$now WHERE k=$from AND valid_to IS NULL", ("$now", now), ("$from", from));
        MutationLog.Append(connection, "node", from, "merge", from, to, $"merged into {to}");

        return $"merged {from} -> {to}.";
    }

    private static void Run(SqliteConnection connection, string sql, params (string name, object? value)[] parameters)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        foreach ((string name, object? value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    private static long ScalarLong(SqliteConnection connection, string sql, params (string name, object? value)[] parameters)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        foreach ((string name, object? value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return (long)(command.ExecuteScalar() ?? 0L);
    }
}
