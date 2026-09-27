using Grimora.Store.Data;
using Grimora.Store.Tools;
using Microsoft.Data.Sqlite;

namespace Grimora.Memory.Tools;

/// <summary>
/// Deletes one memory by key. CLI verb <c>shed-memory</c> and MCP tool <c>shed_memory</c> answer the same
/// job (RESTRUCTURE.md section 2.2). <c>ExecuteCli</c> is copied verbatim from grimora.cs's <c>ShedMemory</c>
/// (grimora.cs:1175, logs the deletion to the mutation log); <c>ExecuteMcp</c> from mcp.cs's <c>shed_memory</c>
/// (mcp.cs:544) — the same delete, done inline instead of through <see cref="MutationLog"/>, kept as-is.
/// </summary>
public sealed class ShedMemoryTool : ITool
{
    public string Name => "shed-memory";
    public string CliVerb => "shed-memory";
    public string McpName => "shed_memory";
    public string Help =>
        "shed-memory --key <slug>             delete one memory by key. MCP shed_memory(key): the same.";

    public string ExecuteCli(SqliteConnection connection, string key)
    {
        string? before = ScalarText(connection, key);
        if (before is null) return $"no memory '{key}'.";

        using (SqliteCommand begin = connection.CreateCommand()) { begin.CommandText = "BEGIN"; begin.ExecuteNonQuery(); }
        Delete(connection, key);
        MutationLog.Append(connection, "memory", key, "delete", before, null, "shed-memory");
        using (SqliteCommand commit = connection.CreateCommand()) { commit.CommandText = "COMMIT"; commit.ExecuteNonQuery(); }

        return $"shed memory '{key}'.";
    }

    public string ExecuteMcp(SqliteConnection connection, string key)
    {
        string? before = ScalarText(connection, key);
        if (before is null) return $"no memory '{key}'.";

        Delete(connection, key);
        MutationLog.Append(connection, "memory", key, "delete", before, null, "shed_memory");

        return $"shed memory '{key}'.";
    }

    private static string? ScalarText(SqliteConnection connection, string key)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT type||'|'||title||'|'||hook||'|'||body FROM memory WHERE k=$k";
        command.Parameters.AddWithValue("$k", key);
        return command.ExecuteScalar() as string;
    }

    private static void Delete(SqliteConnection connection, string key)
    {
        using (SqliteCommand delFts = connection.CreateCommand())
        {
            delFts.CommandText = "DELETE FROM memory_fts WHERE k=$k";
            delFts.Parameters.AddWithValue("$k", key);
            delFts.ExecuteNonQuery();
        }
        using SqliteCommand delRow = connection.CreateCommand();
        delRow.CommandText = "DELETE FROM memory WHERE k=$k";
        delRow.Parameters.AddWithValue("$k", key);
        delRow.ExecuteNonQuery();
    }
}
