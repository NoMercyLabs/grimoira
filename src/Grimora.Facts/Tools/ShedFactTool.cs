using Grimora.Facts.Data;
using Grimora.Store.Data;
using Grimora.Store.Tools;
using Microsoft.Data.Sqlite;

namespace Grimora.Facts.Tools;

/// <summary>Deletes one fact by key. Copied verbatim from grimora.cs's <c>ShedFact</c> (grimora.cs:1191).</summary>
public sealed class ShedFactTool : ITool
{
    public string Name => "shed-fact";
    public string CliVerb => "shed-fact";
    public string? McpName => null;
    public string Help => "shed-fact --key <term>               delete one fact by key";

    public string Execute(SqliteConnection connection, string key)
    {
        string? before = FactRecord.ReadJson(connection, key);
        if (before is null) return $"no fact '{key}'.";

        using (SqliteCommand begin = connection.CreateCommand()) { begin.CommandText = "BEGIN"; begin.ExecuteNonQuery(); }
        Run(connection, "DELETE FROM facts_fts WHERE k=$k", key);
        Run(connection, "DELETE FROM facts WHERE k=$k", key);
        Run(connection, "DELETE FROM ref WHERE channel='facts' AND payload_k=$k", key);
        MutationLog.Append(connection, "fact", key, "delete", before, null, "shed-fact");
        using (SqliteCommand commit = connection.CreateCommand()) { commit.CommandText = "COMMIT"; commit.ExecuteNonQuery(); }

        return $"shed fact '{key}'.";
    }

    private static void Run(SqliteConnection connection, string sql, string key)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$k", key);
        command.ExecuteNonQuery();
    }
}
