using System.Text;
using Aitm.Store.Tools;
using Microsoft.Data.Sqlite;

namespace Aitm.Facts.Tools;

/// <summary>
/// Lists open findings. CLI verb <c>findings</c> and MCP tool <c>open_findings</c> are one job with two
/// shapes (RESTRUCTURE.md section 2.2). <c>ExecuteCli</c> is copied verbatim from aitm.cs's
/// <c>ListRows</c> (aitm.cs:592) call for the <c>findings</c> verb (aitm.cs:222); <c>ExecuteMcp</c> from
/// mcp.cs's <c>open_findings</c> (mcp.cs:902) — latest 30, clipped, budgeted.
/// </summary>
public sealed class FindingsTool : ITool
{
    private const int CellCap = 160;
    private const int SourceCap = 60;

    public string Name => "findings";
    public string CliVerb => "findings";
    public string? McpName => "open_findings";
    public string Help =>
        "findings                              list open findings. MCP open_findings(): latest 30, clipped.";

    public string ExecuteCli(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT id,title FROM findings WHERE status='open' ORDER BY id";
        using SqliteDataReader reader = command.ExecuteReader();
        StringBuilder sb = new();
        int n = 0;
        while (reader.Read()) { sb.AppendLine($"  #{reader.GetInt32(0)}  {reader.GetString(1)}"); n++; }
        sb.Append($"({n} open finding(s))");
        return sb.ToString();
    }

    public string ExecuteMcp(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT id,title,source FROM findings WHERE status='open' ORDER BY id DESC LIMIT 30";
        using SqliteDataReader reader = command.ExecuteReader();
        StringBuilder sb = new();
        while (reader.Read())
            sb.AppendLine($"#{reader.GetInt32(0)} {Clip(reader.GetString(1), CellCap)} ({(reader.IsDBNull(2) ? "" : Clip(reader.GetString(2), SourceCap))})");
        return sb.Length == 0 ? "no open findings." : OutputBudget.Clip(sb.ToString());
    }

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..max].TrimEnd() + "…";
}
