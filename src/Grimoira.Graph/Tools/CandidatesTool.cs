using Grimoira.Store.Tools;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Grimoira.Graph.Tools;

/// <summary>
/// Lists pending edge candidates staged by <see cref="ExtractEdgesTool"/>, optionally filtered to one
/// symbol. Copied verbatim from grimoira.cs's <c>ListCandidates</c> (grimoira.cs:2950-2966).
/// </summary>
public sealed class CandidatesTool : ITool
{
    public string Name => "candidates";
    public string CliVerb => "candidates";
    public string? McpName => null;
    public string Help => "candidates [--symbol <s>]             list pending edge candidates";

    public string Execute(SqliteConnection connection, string? symbol)
    {
        using SqliteCommand c = connection.CreateCommand();
        c.CommandText = symbol is null
            ? "SELECT id,symbol,project,file,line,hardcoded,usage FROM edge_candidates WHERE status='pending' ORDER BY symbol,project,file"
            : "SELECT id,symbol,project,file,line,hardcoded,usage FROM edge_candidates WHERE status='pending' AND symbol=$s ORDER BY project,file";
        if (symbol is not null) c.Parameters.AddWithValue("$s", symbol);
        using SqliteDataReader r = c.ExecuteReader();
        StringBuilder sb = new();
        int n = 0;
        while (r.Read())
        {
            string cu = r.GetString(6);
            sb.AppendLine($"  #{r.GetInt32(0)}  {r.GetString(1)}  {r.GetString(2)} {r.GetString(3)}:{r.GetInt32(4)}{(r.GetInt32(5) == 1 ? "  [HARDCODED]" : "")}{(cu.Length > 0 ? "  " + cu : "")}");
            n++;
        }
        sb.Append($"({n} pending candidate(s) — promote with: grimoira promote <id>)");
        return sb.ToString();
    }
}
