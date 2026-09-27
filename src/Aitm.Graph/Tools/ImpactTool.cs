using Aitm.Store.Tools;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Aitm.Graph.Tools;

/// <summary>
/// Given a symbol/field/contract that is CHANGING, lists every recorded cross-project consumer site,
/// flagging the hardcoded ones that break on a rename. CLI verb <c>impact</c> and MCP tool <c>impact</c>
/// are one job with two shapes today (RESTRUCTURE.md section 2.2), so this tool carries both, each
/// copied verbatim from its own oracle: <c>ExecuteCli</c> from aitm.cs's <c>ImpactCmd</c> (aitm.cs:2482),
/// <c>ExecuteMcp</c> from mcp.cs's <c>impact</c> (mcp.cs:292). The CLI side separates genuine logic
/// consumers from contract-surface sites and groups the latter per project; the MCP side sorts
/// hardcoded-first and counts hardcoded hits inline — kept as separate paths rather than unified, the
/// same way <c>QueryTool</c> keeps its CLI and MCP shapes apart.
/// </summary>
public sealed class ImpactTool : ITool
{
    public string Name => "impact";
    public string CliVerb => "impact";
    public string McpName => "impact";
    public string Help =>
        "impact <symbol>                       list cross-project consumers of a changing symbol/field/contract. " +
        "Call before changing any shared contract.";

    public string ExecuteCli(SqliteConnection connection, string symbol)
    {
        bool hasFileRel = Schema.GraphFileRelSchema.HasColumn(connection, "edges", "file_rel");
        Dictionary<string, string> roots = hasFileRel ? Schema.GraphFileRelSchema.LoadProjectRoots(connection) : [];

        List<(string project, string file, int line, string usage)> edges = [];
        string contract = "";
        using (SqliteCommand c = connection.CreateCommand())
        {
            c.CommandText = hasFileRel
                ? "SELECT project,file,line,usage,contract,file_rel FROM edges WHERE symbol=$s COLLATE NOCASE ORDER BY project, file"
                : "SELECT project,file,line,usage,contract FROM edges WHERE symbol=$s COLLATE NOCASE ORDER BY project, file";
            c.Parameters.AddWithValue("$s", symbol);
            using SqliteDataReader r = c.ExecuteReader();
            while (r.Read())
            {
                contract = r.GetString(4);
                string project = r.GetString(0);
                string? fileRel = hasFileRel && !r.IsDBNull(5) ? r.GetString(5) : null;
                string file = Schema.GraphFileRelSchema.ResolveFull(
                    roots.GetValueOrDefault(project), fileRel, r.GetString(1));
                edges.Add((project, file, r.GetInt32(2), r.GetString(3)));
            }
        }
        if (edges.Count == 0)
            return $"'{symbol}' has no recorded consumers (safe to change, or not yet indexed).";

        List<(string project, string file, int line, string usage)> consumers = [.. edges.Where(e => e.usage.Length > 0)];
        List<(string project, string file, int line, string usage)> contractSites = [.. edges.Where(e => e.usage.Length == 0)];
        int projects = edges.Select(e => e.project).Distinct().Count();

        StringBuilder sb = new();
        sb.AppendLine($"Changing '{symbol}' ({contract}): {consumers.Count} consumer(s) to review, {contractSites.Count} contract site(s) (mechanical), across {projects} project(s).");
        foreach ((string project, string file, int line, string usage) in consumers)
            sb.AppendLine($"  consumer  {project,-8} {file}:{line}  {usage}");
        foreach (IGrouping<string, (string project, string file, int line, string usage)> g in contractSites.GroupBy(e => e.project))
            sb.AppendLine($"  contract  {g.Key,-8} {string.Join(", ", g.Select(e => Path.GetFileName(e.file) + ":" + e.line))}");
        sb.Append("  => reconcile in lockstep; never break existing users.");
        return sb.ToString();
    }

    public string ExecuteMcp(SqliteConnection connection, string symbol)
    {
        bool hasFileRel = Schema.GraphFileRelSchema.HasColumn(connection, "edges", "file_rel");
        Dictionary<string, string> roots = hasFileRel ? Schema.GraphFileRelSchema.LoadProjectRoots(connection) : [];

        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = hasFileRel
            ? "SELECT project,file,line,usage,hardcoded,contract,file_rel FROM edges WHERE symbol=$s COLLATE NOCASE ORDER BY hardcoded DESC, project, file"
            : "SELECT project,file,line,usage,hardcoded,contract FROM edges WHERE symbol=$s COLLATE NOCASE ORDER BY hardcoded DESC, project, file";
        cmd.Parameters.AddWithValue("$s", symbol);
        using SqliteDataReader r = cmd.ExecuteReader();
        StringBuilder sb = new();
        int hard = 0;
        string contract = "";
        while (r.Read())
        {
            bool h = r.GetInt32(4) == 1;
            if (h) hard++;
            contract = r.GetString(5);
            string project = r.GetString(0);
            string? fileRel = hasFileRel && !r.IsDBNull(6) ? r.GetString(6) : null;
            string file = Schema.GraphFileRelSchema.ResolveFull(
                roots.GetValueOrDefault(project), fileRel, r.GetString(1));
            sb.AppendLine($"  {project} {file}:{r.GetInt32(2)} {(h ? "[HARDCODED] " : "")}{r.GetString(3)}");
        }
        if (sb.Length == 0) return $"'{symbol}' has no recorded consumers (safe to change, or not yet indexed).";
        return $"Changing '{symbol}' ({contract}) impacts these consumers ({hard} hardcoded — break on rename; surface for lockstep, never break existing users):\n{sb}";
    }
}
