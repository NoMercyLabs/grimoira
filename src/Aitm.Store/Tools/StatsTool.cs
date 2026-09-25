using System.Text;
using Microsoft.Data.Sqlite;

namespace Aitm.Store.Tools;

/// <summary>Copied verbatim from aitm.cs's <c>Stats</c> (aitm.cs:3025-3037).</summary>
public sealed class StatsTool : ITool
{
    public string Name => "stats";
    public string CliVerb => "stats";
    public string? McpName => null;
    public string Help => "stats                                counts across every channel";

    public string Execute(SqliteConnection connection, string instance, string dbPath)
    {
        long Count(string sql)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = sql;
            return (long)(command.ExecuteScalar() ?? 0L);
        }

        StringBuilder sb = new();
        sb.AppendLine($"instance '{instance}'  ({dbPath})");
        sb.AppendLine($"  facts      {Count("SELECT count(*) FROM facts")}");
        sb.AppendLine($"  edges      {Count("SELECT count(*) FROM edges")}  ({Count("SELECT count(DISTINCT symbol) FROM edges")} symbols)");
        sb.AppendLine($"  chat       {Count("SELECT count(*) FROM chat")}");
        sb.AppendLine($"  docs       {Count("SELECT count(*) FROM docs")} sections ({Count("SELECT count(DISTINCT path) FROM docs")} docs)");
        sb.AppendLine($"  memory     {Count("SELECT count(*) FROM memory")}  ({Count("SELECT count(*) FROM memory WHERE hard=1")} hard)");
        sb.AppendLine($"  mutations  {Count("SELECT count(*) FROM mutations")}");
        sb.AppendLine($"  todos      {Count("SELECT count(*) FROM todos WHERE status='open'")} open");
        sb.AppendLine($"  findings   {Count("SELECT count(*) FROM findings WHERE status='open'")} open / {Count("SELECT count(*) FROM findings")} total");
        sb.Append($"  projects   {Count("SELECT count(*) FROM projects")}");
        return sb.ToString();
    }
}
