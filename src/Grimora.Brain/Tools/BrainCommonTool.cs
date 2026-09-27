using Grimora.Brain.Data;
using Grimora.Store.Tools;
using Microsoft.Data.Sqlite;

namespace Grimora.Brain.Tools;

/// <summary>
/// Intersection — what the given projects share. Coverage-tolerant: returns shared_by + present_n + the
/// unresolved list, so a near-miss surfaces and a typo'd project is echoed, never silently dropped. CLI
/// sub-verb <c>brain common</c> and MCP tool <c>brain_common</c> are one job with two shapes today
/// (RESTRUCTURE.md section 2.2), so this tool carries both, each copied verbatim from its own oracle:
/// <c>ExecuteCli</c> from grimora.cs's <c>BrainCommon</c> (grimora.cs:1456), <c>ExecuteMcp</c> from mcp.cs's
/// <c>brain_common</c> (mcp.cs:686). The CLI side selects 7 columns including <c>scheme</c>; the MCP side
/// selects 6 and caps rows to 24 — kept as separate paths rather than unified, the same way
/// <c>ImpactTool</c> keeps its CLI and MCP shapes apart.
/// </summary>
public sealed class BrainCommonTool : ITool
{
    public string Name => "brain common";
    public string CliVerb => "brain common";
    public string McpName => "brain_common";
    public string Help =>
        "brain common <projA> <projB> [projC ...]   what the named projects have in COMMON. " +
        "Coverage-tolerant: shared_by + present_n + unresolved list. Needs 2+ projects.";

    public string ExecuteCli(SqliteConnection connection, IReadOnlyList<string> projects)
    {
        if (projects.Count < 2) return "usage: brain common <projA> <projB> [projC ...]";
        List<string> keys = [.. projects.Select(p => BrainProjects.Normalize(connection, p))];
        string values = string.Join(",", keys.Select((_, i) => $"($p{i})"));
        using SqliteCommand c = connection.CreateCommand();
        c.CommandText = $@"WITH req(name) AS (VALUES {values}),
            present AS (SELECT DISTINCT n.k FROM req JOIN node_now n ON n.k = req.name)
            SELECT t.o AS shared, n.label, n.gloss, n.scheme,
                   COUNT(DISTINCT t.s) AS shared_by,
                   (SELECT COUNT(*) FROM present) AS present_n,
                   (SELECT group_concat(name) FROM req WHERE name NOT IN (SELECT k FROM present)) AS unresolved
            FROM triple_now t
            JOIN present pr ON pr.k = t.s
            JOIN node_now n ON n.k = t.o
            WHERE t.p IN (SELECT p FROM pred_vocab WHERE is_sharing=1) AND t.o_is_literal = 0
            GROUP BY t.o
            HAVING COUNT(DISTINCT t.s) >= 2
            ORDER BY shared_by DESC, n.scheme, n.label";
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
        if (keys.Count < 2) return "give 2+ projects (e.g. 'web android ios').";
        string values = string.Join(",", keys.Select((_, i) => $"($p{i})"));
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = $@"WITH req(name) AS (VALUES {values}),
            present AS (SELECT DISTINCT n.k FROM req JOIN node_now n ON n.k = req.name)
            SELECT t.o AS shared, n.label, n.gloss,
                   COUNT(DISTINCT t.s) AS shared_by, (SELECT COUNT(*) FROM present) AS present_n,
                   (SELECT group_concat(name) FROM req WHERE name NOT IN (SELECT k FROM present)) AS unresolved
            FROM triple_now t JOIN present pr ON pr.k = t.s JOIN node_now n ON n.k = t.o
            WHERE t.p IN (SELECT p FROM pred_vocab WHERE is_sharing=1) AND t.o_is_literal = 0
            GROUP BY t.o HAVING COUNT(DISTINCT t.s) >= 2 ORDER BY shared_by DESC, n.scheme, n.label LIMIT 24";
        for (int i = 0; i < keys.Count; i++) cmd.Parameters.AddWithValue($"$p{i}", keys[i]);
        return BrainMcpRows.RowsK(connection, cmd);
    }
}
