using System.Text;
using Microsoft.Data.Sqlite;

namespace Grimora.Store.Tools;

/// <summary>
/// The cold, append-only mutation log for one entity. RESTRUCTURE.md section 2.2: the CLI verb and the
/// MCP tool share a name but not a shape today, so both are kept, copied verbatim (CLI: grimora.cs:2435,
/// ascending, unbounded; MCP: mcp.cs:871, descending, latest 30, budgeted).
/// </summary>
public sealed class HistoryTool : ITool
{
    public string Name => "history";
    public string CliVerb => "history";
    public string McpName => "history";
    public string Help =>
        "Show the change history (the cold append-only mutation log) for an entity, for tracing when " +
        "something went wrong. CLI: full log, oldest first. MCP: latest 30, newest first.";

    public string ExecuteCli(SqliteConnection connection, string term)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT ts,op,k FROM mutations WHERE k LIKE $t ORDER BY id";
        command.Parameters.AddWithValue("$t", "%" + term + "%");
        using SqliteDataReader reader = command.ExecuteReader();
        StringBuilder sb = new();
        int n = 0;
        while (reader.Read())
        {
            sb.AppendLine($"  {reader.GetString(0)}  {reader.GetString(1),-6}  {reader.GetString(2)}");
            n++;
        }
        sb.Append(n == 0 ? $"no history for '{term}'." : $"({n} mutation(s) in the cold log)");
        return sb.ToString();
    }

    public string ExecuteMcp(SqliteConnection connection, string term)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT ts,op,k FROM mutations WHERE k LIKE $t ORDER BY id DESC LIMIT 30";
        command.Parameters.AddWithValue("$t", "%" + term + "%");
        using SqliteDataReader reader = command.ExecuteReader();
        StringBuilder sb = new();
        while (reader.Read())
            sb.AppendLine($"  {reader.GetString(0)} {reader.GetString(1)} {reader.GetString(2)}");
        return sb.Length == 0 ? $"no history for '{term}'." : OutputBudget.Clip(sb.ToString());
    }
}
