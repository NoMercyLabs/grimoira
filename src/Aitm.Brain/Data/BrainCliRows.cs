using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Aitm.Brain.Data;

/// <summary>
/// Row formatting for the CLI side of the brain reads. Copied verbatim from aitm.cs's <c>PrintReader</c>
/// (aitm.cs:1376) and <c>RunReader</c> (aitm.cs:1392) — shared by <c>brain core</c>, <c>brain scope</c>,
/// <c>brain common</c> and <c>brain place</c> rather than four near-identical duplicates.
/// </summary>
public static class BrainCliRows
{
    private static string Cols(SqliteDataReader r)
    {
        List<string> cols = new();
        for (int i = 0; i < r.FieldCount; i++)
            cols.Add(r.IsDBNull(i) ? "" : Convert.ToString(r.GetValue(i), CultureInfo.InvariantCulture) ?? "");
        return string.Join("  |  ", cols);
    }

    /// <summary>Prints every row, unclipped, undeduplicated. Used where the caller never reinforces
    /// (<c>brain core</c>).</summary>
    public static string PrintReader(SqliteDataReader r)
    {
        StringBuilder sb = new();
        int n = 0;
        while (r.Read())
        {
            sb.AppendLine("  " + Cols(r));
            n++;
        }
        if (n == 0) sb.AppendLine("  (nothing)");
        return sb.ToString();
    }

    /// <summary>Execute, print, and return each row's first column (the node key for a brain read), so
    /// the caller can reinforce after the reader is disposed.</summary>
    public static (string output, List<string> keys) RunReader(SqliteCommand c, bool announceEmpty = true)
    {
        List<string> keys = new();
        StringBuilder sb = new();
        int n = 0;
        using (SqliteDataReader r = c.ExecuteReader())
        {
            while (r.Read())
            {
                List<string> cols = new();
                for (int i = 0; i < r.FieldCount; i++)
                    cols.Add(r.IsDBNull(i) ? "" : Convert.ToString(r.GetValue(i), CultureInfo.InvariantCulture) ?? "");
                if (cols.Count > 0) keys.Add(cols[0]);
                sb.AppendLine("  " + string.Join("  |  ", cols));
                n++;
            }
            if (n == 0 && announceEmpty) sb.AppendLine("  (nothing)");
        }
        return (sb.ToString(), keys);
    }
}
