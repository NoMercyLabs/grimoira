using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.ML.Tokenizers;

namespace Grimora.Store.Tools;

/// <summary>
/// Copied verbatim from grimora.cs's <c>Stats</c> (grimora.cs:3025-3037). <c>--tokens</c> absorbs the old
/// standalone <c>tok.cs</c> (RESTRUCTURE.md slice 24 bullet 3): the real BPE token cost of stored
/// content (o200k_base, a close proxy for what an LLM context actually pays), copied verbatim from
/// tok.cs's own <c>Sum</c>/main body — same columns summed, same output shape, one instance argument.
/// </summary>
public sealed class StatsTool : ITool
{
    public string Name => "stats";
    public string CliVerb => "stats";
    public string? McpName => null;
    public string Help => "stats [--tokens]                     counts across every channel; --tokens: real BPE token cost (o200k_base) of docs + facts";

    public string ExecuteTokens(SqliteConnection connection, string instance)
    {
        Tokenizer tok = TiktokenTokenizer.CreateForModel("gpt-4o");

        long Sum(string table, string column)
        {
            long total = 0;
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = $"SELECT {column} FROM {table}";
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
                if (!reader.IsDBNull(0)) total += tok.CountTokens(reader.GetString(0));
            return total;
        }

        long docTokens = Sum("docs", "content");
        long factTokens = Sum("facts", "value");
        long docCount = Count(connection, "SELECT count(*) FROM docs");

        StringBuilder sb = new();
        sb.AppendLine($"instance '{instance}' token cost (o200k_base):");
        sb.AppendLine($"  docs:  {docTokens,8} tok across {docCount} sections  (avg {(docCount == 0 ? 0 : docTokens / docCount)}/section)");
        sb.Append($"  facts: {factTokens,8} tok");
        return sb.ToString();
    }

    private static long Count(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)(command.ExecuteScalar() ?? 0L);
    }

    public string Execute(SqliteConnection connection, string instance, string dbPath)
    {
        StringBuilder sb = new();
        sb.AppendLine($"instance '{instance}'  ({dbPath})");
        sb.AppendLine($"  facts      {Count(connection, "SELECT count(*) FROM facts")}");
        sb.AppendLine($"  edges      {Count(connection, "SELECT count(*) FROM edges")}  ({Count(connection, "SELECT count(DISTINCT symbol) FROM edges")} symbols)");
        sb.AppendLine($"  chat       {Count(connection, "SELECT count(*) FROM chat")}");
        sb.AppendLine($"  docs       {Count(connection, "SELECT count(*) FROM docs")} sections ({Count(connection, "SELECT count(DISTINCT path) FROM docs")} docs)");
        sb.AppendLine($"  memory     {Count(connection, "SELECT count(*) FROM memory")}  ({Count(connection, "SELECT count(*) FROM memory WHERE hard=1")} hard)");
        sb.AppendLine($"  mutations  {Count(connection, "SELECT count(*) FROM mutations")}");
        sb.AppendLine($"  todos      {Count(connection, "SELECT count(*) FROM todos WHERE status='open'")} open");
        sb.AppendLine($"  findings   {Count(connection, "SELECT count(*) FROM findings WHERE status='open'")} open / {Count(connection, "SELECT count(*) FROM findings")} total");
        sb.Append($"  projects   {Count(connection, "SELECT count(*) FROM projects")}");
        return sb.ToString();
    }
}
