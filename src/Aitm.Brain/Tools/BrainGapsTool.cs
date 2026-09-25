using Aitm.Brain.Data;
using Microsoft.Data.Sqlite;
using Aitm.Store.Tools;

namespace Aitm.Brain.Tools;

/// <summary>
/// Lists what the brain could NOT answer — every refused/empty lookup is auto-logged to the <c>gaps</c>
/// table (RESTRUCTURE.md section 1, gap log); a later learn whose text covers a gap's tokens auto-resolves
/// it. CLI sub-verb <c>brain gaps</c> and MCP tool <c>brain_gaps</c> are one job with two shapes today
/// (RESTRUCTURE.md section 2.2), so this tool carries both, each copied verbatim from its own oracle:
/// <c>ExecuteCli</c> from aitm.cs's <c>BrainGaps</c> (aitm.cs:1596), <c>ExecuteMcp</c> from mcp.cs's
/// <c>brain_gaps</c> (mcp.cs:860). The CLI side lists up to 25 rows with a trailing hint; the MCP side
/// lists up to 15 with an open-count header when there are more — kept as separate paths rather than
/// unified, the same way the other brain reads keep their CLI and MCP shapes apart.
/// </summary>
public sealed class BrainGapsTool : ITool
{
    public string Name => "brain gaps";
    public string CliVerb => "brain gaps";
    public string? McpName => "brain_gaps";
    public string Help =>
        "brain gaps                             what the brain could NOT answer — every refused fact/" +
        "rule/recall/doc/place query is auto-logged here. Check at session start. No args.";

    public string ExecuteCli(SqliteConnection connection)
    {
        System.Text.StringBuilder sb = new();
        using SqliteCommand c = connection.CreateCommand();
        c.CommandText = "SELECT misses, tool, query, substr(last_ts,1,10) FROM gaps WHERE status='open' ORDER BY misses DESC, last_ts DESC LIMIT 25";
        using SqliteDataReader r = c.ExecuteReader();
        int n = 0;
        while (r.Read()) { sb.AppendLine($"  {r.GetInt32(0)}x [{r.GetString(1)}] {r.GetString(2)}  (last {r.GetString(3)})"); n++; }
        sb.AppendLine(n == 0 ? "  no open gaps." : $"  ({n} open gap(s) — answer one and `brain learn` it; a matching learn auto-resolves)");
        // sb already ends with one AppendLine terminator; the CLI dispatch wraps this return value in one
        // more Console.WriteLine, so strip that one terminator here (same fix as part 2's
        // MemTool/DocTool/RecallTool) rather than double it into an extra blank line.
        return StripOneTrailingNewLine(sb.ToString());
    }

    private static string StripOneTrailingNewLine(string s) =>
        s.EndsWith(Environment.NewLine, StringComparison.Ordinal) ? s[..^Environment.NewLine.Length] : s;

    public string ExecuteMcp(SqliteConnection connection)
    {
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT misses||'x', tool, query, substr(last_ts,1,10) FROM gaps WHERE status='open' ORDER BY misses DESC, last_ts DESC LIMIT 15";
        string rows = BrainMcpRows.Rows(cmd);
        if (rows == "(nothing)") return "no open gaps — every recent lookup was answerable.";
        long open = Count(connection, "SELECT count(*) FROM gaps WHERE status='open'");
        return (open > 15 ? $"{open} open gaps, top 15:\n" : "") + rows;
    }

    private static long Count(SqliteConnection connection, string sql)
    {
        using SqliteCommand c = connection.CreateCommand();
        c.CommandText = sql;
        return (long)(c.ExecuteScalar() ?? 0L);
    }
}
