using System.Globalization;
using System.Text;
using Grimora.Store.Tools;
using Microsoft.Data.Sqlite;

namespace Grimora.Brain.Data;

/// <summary>
/// Row formatting for the MCP side of the brain reads (<c>brain_core</c>, <c>brain_scope</c>,
/// <c>brain_common</c>, <c>brain_place</c>). Copied verbatim from mcp.cs's <c>RowLine</c> (mcp.cs:581),
/// <c>Rows</c> (mcp.cs:603) and <c>RowsK</c> (mcp.cs:618) — all four brain read tools share this one copy
/// rather than four near-identical duplicates. <see cref="OutputBudget"/> already carries mcp.cs's
/// <c>Budget</c> (Store owns the output budget, RESTRUCTURE.md section 1).
/// </summary>
public static class BrainMcpRows
{
    private const int CellCap = 160;

    /// <summary>One result line per row: empty cells dropped, a cell that merely repeats or prefixes its
    /// neighbour dropped (k == label, label == gloss-head are the common cases), every cell clipped.
    /// Prefix-dedupe only kicks in past 3 chars so short numeric cells (flags, counts) survive.</summary>
    public static string RowLine(SqliteDataReader r)
    {
        List<string> raw = [];
        for (int i = 0; i < r.FieldCount; i++)
        {
            string v = r.IsDBNull(i) ? "" : Convert.ToString(r.GetValue(i), CultureInfo.InvariantCulture) ?? "";
            if (v.Length > 0) raw.Add(v);
        }
        List<string> kept = [];
        for (int i = 0; i < raw.Count; i++)
        {
            string v = raw[i];
            bool nextExtends = i + 1 < raw.Count && v.Length > 3 && raw[i + 1].StartsWith(v, StringComparison.OrdinalIgnoreCase);
            bool prevCovers = kept.Count > 0 && (string.Equals(kept[^1], v, StringComparison.OrdinalIgnoreCase)
                || (v.Length > 3 && kept[^1].StartsWith(v, StringComparison.OrdinalIgnoreCase))
                || kept[^1].EndsWith(":" + v, StringComparison.OrdinalIgnoreCase));
            if (nextExtends || prevCovers) continue;
            kept.Add(v);
        }
        return kept.Count > 0 ? "• " + string.Join("  |  ", kept.Select(v => Clip(v, CellCap))) : "";
    }

    public static string Rows(SqliteCommand cmd, int cap = OutputBudget.DefaultCap)
    {
        using SqliteDataReader r = cmd.ExecuteReader();
        StringBuilder sb = new();
        while (r.Read())
        {
            string line = RowLine(r);
            if (line.Length > 0) sb.AppendLine(line);
        }
        return sb.Length == 0 ? "(nothing)" : OutputBudget.Clip(sb.ToString(), cap);
    }

    /// <summary>Like <see cref="Rows"/>, but also reinforces each row's first column (the surfaced node
    /// key). The reader is disposed before the write, so reinforcement never races the open reader on
    /// the single connection.</summary>
    public static string RowsK(SqliteConnection connection, SqliteCommand cmd)
    {
        StringBuilder sb = new();
        List<string> keys = [];
        using (SqliteDataReader r = cmd.ExecuteReader())
        {
            while (r.Read())
            {
                if (!r.IsDBNull(0))
                {
                    string k0 = Convert.ToString(r.GetValue(0), CultureInfo.InvariantCulture) ?? "";
                    if (k0.Length > 0) keys.Add(k0);
                }
                string line = RowLine(r);
                if (line.Length > 0) sb.AppendLine(line);
            }
        }
        BrainUsage.Reinforce(connection, keys);
        return sb.Length == 0 ? "(nothing)" : OutputBudget.Clip(sb.ToString());
    }

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..max].TrimEnd() + "…";
}
