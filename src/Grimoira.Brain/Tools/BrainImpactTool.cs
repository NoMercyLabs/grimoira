using System.Text;
using Grimoira.Brain.Data;
using Grimoira.Graph.Schema;
using Grimoira.Store.Tools;
using Microsoft.Data.Sqlite;

namespace Grimoira.Brain.Tools;

/// <summary>
/// Cross-project impact — every consumer of a shared symbol/contract from the still-live, alias-corrected
/// edges view plus the summary triple. Call before changing a shared contract. CLI sub-verb
/// <c>brain impact</c> and MCP tool <c>brain_impact</c> are one job with two shapes today (RESTRUCTURE.md
/// section 2.2), so this tool carries both, each copied verbatim from its own oracle: <c>ExecuteCli</c>
/// from grimoira.cs's <c>BrainImpact</c> (grimoira.cs:1566), <c>ExecuteMcp</c> from mcp.cs's <c>brain_impact</c>
/// (mcp.cs:769). The CLI side has no row cap; the MCP side caps at 40 rows — kept as separate paths
/// rather than unified, the same way <see cref="Grimoira.Graph.Tools.ImpactTool"/> (the unrelated top-level
/// <c>impact</c> verb over the graph edges table) keeps its CLI and MCP shapes apart.
/// </summary>
public sealed class BrainImpactTool : ITool
{
    public string Name => "brain impact";
    public string CliVerb => "brain impact";
    public string McpName => "brain_impact";
    public bool IsReadOnly => true;
    public string Help =>
        "brain impact <symbol-or-contract>     every cross-project consumer of a shared symbol/contract " +
        "from the live edges graph plus the summary triple. Call BEFORE changing any shared contract.";

    public string ExecuteCli(SqliteConnection connection, string term)
    {
        if (term.Trim().Length == 0) return "usage: brain impact <symbol-or-contract>";
        if (!HasFileRel(connection))
        {
            using SqliteCommand c = connection.CreateCommand();
            c.CommandText = """
                SELECT s AS project, o AS contract_symbol, file, line, hardcoded
                FROM legacy_consumes WHERE o LIKE $like
                UNION ALL
                SELECT substr(t.s,6), t.o, NULL, NULL, NULL
                FROM triple_now t
                WHERE t.p='consumes' AND t.o LIKE $like AND t.o_is_literal=0
                """;
            c.Parameters.AddWithValue("$like", "%" + term + "%");
            (string output, List<string> keys) = BrainCliRows.RunReader(c);
            BrainUsage.Reinforce(connection, keys);
            return output;
        }

        // file_rel (slice 31b): resolve each row's file to a full path for THIS machine — the same
        // fallback-to-file rule the graph_* tools and impact use — before printing, rather than the
        // legacy_consumes view's own (possibly stale, from a moved root) file column.
        Dictionary<string, string> roots = GraphFileRelSchema.LoadProjectRoots(connection);
        List<string> keys2 = [];
        StringBuilder sb = new();
        int n = 0;
        using (SqliteCommand c = connection.CreateCommand())
        {
            c.CommandText = """
                SELECT s, o, file, line, hardcoded, project, file_rel
                FROM legacy_consumes WHERE o LIKE $like
                UNION ALL
                SELECT substr(t.s,6), t.o, NULL, NULL, NULL, NULL, NULL
                FROM triple_now t
                WHERE t.p='consumes' AND t.o LIKE $like AND t.o_is_literal=0
                """;
            c.Parameters.AddWithValue("$like", "%" + term + "%");
            using SqliteDataReader r = c.ExecuteReader();
            while (r.Read())
            {
                string s = r.GetString(0);
                keys2.Add(s);
                string o = r.IsDBNull(1) ? "" : r.GetString(1);
                string resolvedFile = ResolveRowFile(r, roots, fileOrdinal: 2, projectOrdinal: 5, fileRelOrdinal: 6);
                string line = r.IsDBNull(3) ? "" : r.GetInt32(3).ToString();
                string hardcoded = r.IsDBNull(4) ? "" : r.GetInt32(4).ToString();
                sb.AppendLine("  " + string.Join("  |  ", new[] { s, o, resolvedFile, line, hardcoded }));
                n++;
            }
        }
        if (n == 0) sb.AppendLine("  (nothing)");
        BrainUsage.Reinforce(connection, keys2);
        return sb.ToString();
    }

    public string ExecuteMcp(SqliteConnection connection, string symbol)
    {
        if (!HasFileRel(connection))
        {
            using SqliteCommand cmd = connection.CreateCommand();
            cmd.CommandText = """
                SELECT s AS project, o AS contract_symbol, file, line, hardcoded FROM legacy_consumes WHERE o LIKE $like
                UNION ALL SELECT substr(t.s,6), t.o, NULL, NULL, NULL FROM triple_now t WHERE t.p='consumes' AND t.o LIKE $like AND t.o_is_literal=0
                LIMIT 40
                """;
            cmd.Parameters.AddWithValue("$like", "%" + symbol + "%");
            return BrainMcpRows.RowsK(connection, cmd);
        }

        Dictionary<string, string> roots = GraphFileRelSchema.LoadProjectRoots(connection);
        using SqliteCommand mcmd = connection.CreateCommand();
        mcmd.CommandText = """
            SELECT s, o, file, line, hardcoded, project, file_rel FROM legacy_consumes WHERE o LIKE $like
            UNION ALL SELECT substr(t.s,6), t.o, NULL, NULL, NULL, NULL, NULL FROM triple_now t WHERE t.p='consumes' AND t.o LIKE $like AND t.o_is_literal=0
            LIMIT 40
            """;
        mcmd.Parameters.AddWithValue("$like", "%" + symbol + "%");
        List<string> keys = [];
        StringBuilder sb = new();
        using (SqliteDataReader r = mcmd.ExecuteReader())
        {
            while (r.Read())
            {
                string s = r.GetString(0);
                if (s.Length > 0) keys.Add(s);
                string o = r.IsDBNull(1) ? "" : r.GetString(1);
                string resolvedFile = ResolveRowFile(r, roots, fileOrdinal: 2, projectOrdinal: 5, fileRelOrdinal: 6);
                string line = r.IsDBNull(3) ? "" : r.GetInt32(3).ToString();
                string hardcoded = r.IsDBNull(4) ? "" : r.GetInt32(4).ToString();
                List<string> cells = [];
                foreach (string v in new[] { s, o, resolvedFile, line, hardcoded })
                    if (v.Length > 0) cells.Add(v);
                if (cells.Count > 0) sb.AppendLine("• " + string.Join("  |  ", cells));
            }
        }
        BrainUsage.Reinforce(connection, keys);
        return sb.Length == 0 ? "(nothing)" : OutputBudget.Clip(sb.ToString());
    }

    private static bool HasFileRel(SqliteConnection connection) =>
        GraphFileRelSchema.HasColumn(connection, "legacy_consumes", "file_rel");

    private static string ResolveRowFile(SqliteDataReader r, Dictionary<string, string> roots, int fileOrdinal, int projectOrdinal, int fileRelOrdinal)
    {
        if (r.IsDBNull(fileOrdinal)) return "";
        string file = r.GetString(fileOrdinal);
        string? project = r.IsDBNull(projectOrdinal) ? null : r.GetString(projectOrdinal);
        string? fileRel = r.IsDBNull(fileRelOrdinal) ? null : r.GetString(fileRelOrdinal);
        return GraphFileRelSchema.ResolveFull(
            project is not null && roots.TryGetValue(project, out string? root) ? root : null, fileRel, file);
    }
}
