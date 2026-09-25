using Aitm.Store.Tools;
using Microsoft.Data.Sqlite;

namespace Aitm.Brain.Tools;

/// <summary>
/// Surfaces knowledge that was asserted long ago and never re-confirmed — the candidates most likely to
/// have rotted as the code drifted. CLI-only sub-verb (RESTRUCTURE.md section 2.2 lists no MCP counterpart
/// for <c>brain stale</c>). Copied verbatim from aitm.cs's <c>BrainStale</c> (aitm.cs:1783).
/// </summary>
public sealed class BrainStaleTool : ITool
{
    private const int DefaultDays = 30;

    public string Name => "brain stale";
    public string CliVerb => "brain stale";
    public string? McpName => null;
    public string Help =>
        "brain stale [--days N]                 nodes older than N days (default 30) and never " +
        "re-confirmed against current code. Re-check, then: aitm brain verify <key>.";

    public string Execute(SqliteConnection connection, int days = DefaultDays)
    {
        System.Text.StringBuilder sb = new();
        using SqliteCommand c = connection.CreateCommand();
        c.CommandText = """
            SELECT n.k, CAST(julianday('now')-julianday(n.valid_from) AS INT) AS age_days,
                    CAST(julianday('now')-julianday(u.verified_at) AS INT) AS since_verified
                FROM node_now n LEFT JOIN usage u ON u.node_k=n.k
                WHERE julianday('now')-julianday(n.valid_from) > $d
                  AND (u.verified_at IS NULL OR julianday('now')-julianday(u.verified_at) > $d)
                ORDER BY age_days DESC LIMIT 25
            """;
        c.Parameters.AddWithValue("$d", days);
        using SqliteDataReader r = c.ExecuteReader();
        int n = 0;
        while (r.Read())
        {
            sb.AppendLine($"  {r.GetInt32(1),5}d  {r.GetString(0)}  ({(r.IsDBNull(2) ? "never confirmed" : r.GetInt32(2) + "d since confirm")})");
            n++;
        }
        sb.AppendLine($"  ({n} node(s) older than {days}d and unconfirmed; re-check, then: aitm brain verify <key>)");
        // sb already ends with one AppendLine terminator; the CLI dispatch wraps this return value in one
        // more Console.WriteLine, so strip that one terminator here (same fix as part 2's
        // MemTool/DocTool/RecallTool) rather than double it into an extra blank line.
        return StripOneTrailingNewLine(sb.ToString());
    }

    private static string StripOneTrailingNewLine(string s) =>
        s.EndsWith(Environment.NewLine, StringComparison.Ordinal) ? s[..^Environment.NewLine.Length] : s;
}
