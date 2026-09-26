using Aitm.Brain.Data;
using Aitm.Store.Tools;
using Microsoft.Data.Sqlite;

namespace Aitm.Brain.Tools;

/// <summary>
/// Scoping — everything spanning the given projects, shared concerns floated to top (scope=N), direction
/// preserved (who exposes vs who consumes), salience-ordered. CLI sub-verb <c>brain scope</c> and MCP
/// tool <c>brain_scope</c> are one job with two shapes today (RESTRUCTURE.md section 2.2), so this tool
/// carries both, each copied verbatim from its own oracle: <c>ExecuteCli</c> from aitm.cs's
/// <c>BrainScope</c> (aitm.cs:1433), <c>ExecuteMcp</c> from mcp.cs's <c>brain_scope</c> (mcp.cs:666). The
/// CLI side selects 8 columns including an unclipped <c>weight</c>; the MCP side selects 7, clips
/// <c>who_and_how</c> to 120 chars in SQL and caps rows to 24 — kept as separate paths rather than
/// unified, the same way <c>ImpactTool</c> keeps its CLI and MCP shapes apart.
/// </summary>
public sealed class BrainScopeTool : ITool
{
    public string Name => "brain scope";
    public string CliVerb => "brain scope";
    public string? McpName => "brain_scope";
    public string Help =>
        "brain scope <projectA> <projectB> [...]   everything in scope for working across the named " +
        "projects, shared seams floated to top with direction. Use before any cross-project task.";

    public string ExecuteCli(SqliteConnection connection, IReadOnlyList<string> projects)
    {
        if (projects.Count < 1) return "usage: brain scope <projectA> <projectB> [...]";
        List<string> keys = [.. projects.Select(p => BrainProjects.Normalize(connection, p))];
        string values = string.Join(",", keys.Select((_, i) => $"($p{i})"));
        using SqliteCommand c = connection.CreateCommand();
        c.CommandText = $@"WITH targets(k) AS (VALUES {values})
            SELECT t.o AS topic, n.label, n.gloss, n.scheme, n.hard,
                   COUNT(DISTINCT t.s) AS scope,
                   group_concat(DISTINCT t.s||':'||t.p) AS who_and_how,
                   ROUND(SUM(t.conf),2) AS weight
            FROM triple_now t
            JOIN targets g ON g.k = t.s
            JOIN node_now n ON n.k = t.o
            WHERE t.p IN (SELECT p FROM pred_vocab WHERE is_sharing=1) AND t.o_is_literal = 0
            GROUP BY t.o
            ORDER BY n.hard DESC, scope DESC, weight DESC, n.scheme";
        for (int i = 0; i < keys.Count; i++) c.Parameters.AddWithValue($"$p{i}", keys[i]);
        (string output, List<string> rowKeys) = BrainCliRows.RunReader(c);
        BrainUsage.Reinforce(connection, rowKeys);
        // RunReader's output already ends with one AppendLine terminator; the CLI dispatch wraps this
        // return value in one more Console.WriteLine, so strip that one terminator here (same fix as
        // part 2's MemTool/DocTool/RecallTool) rather than double it into an extra blank line.
        return StripOneTrailingNewLine(output);
    }

    private static string StripOneTrailingNewLine(string s) =>
        s.EndsWith(Environment.NewLine, StringComparison.Ordinal) ? s[..^Environment.NewLine.Length] : s;

    public string ExecuteMcp(SqliteConnection connection, string projects)
    {
        List<string> keys = [.. BrainProjects.SplitArgs(projects).Select(p => BrainProjects.Normalize(connection, p))];
        if (keys.Count < 1) return "give 1+ project (e.g. 'server web').";
        string values = string.Join(",", keys.Select((_, i) => $"($p{i})"));
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = $@"WITH targets(k) AS (VALUES {values})
            SELECT t.o AS topic, n.label, n.gloss, n.scheme, n.hard,
                   COUNT(DISTINCT t.s) AS scope,
                   substr(group_concat(DISTINCT t.s||':'||t.p),1,120) AS who_and_how
            FROM triple_now t JOIN targets g ON g.k = t.s JOIN node_now n ON n.k = t.o
            WHERE t.p IN (SELECT p FROM pred_vocab WHERE is_sharing=1) AND t.o_is_literal = 0
            GROUP BY t.o ORDER BY n.hard DESC, scope DESC, SUM(t.conf) DESC, n.scheme LIMIT 24";
        for (int i = 0; i < keys.Count; i++) cmd.Parameters.AddWithValue($"$p{i}", keys[i]);
        return BrainMcpRows.RowsK(connection, cmd);
    }
}
