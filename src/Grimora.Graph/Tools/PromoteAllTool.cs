using System.Text;
using Grimora.Store.Data;
using Grimora.Store.Tools;
using Microsoft.Data.Sqlite;

namespace Grimora.Graph.Tools;

/// <summary>
/// Authoritative bulk promotion: the reviewed candidate set for a symbol BECOMES the graph, replacing
/// any prior edges. Copied verbatim from grimora.cs's <c>PromoteAll</c> (grimora.cs:2999-3025), which calls
/// <c>PromoteCandidate</c> per id — <see cref="PromoteTool"/> here.
///
/// RESTRUCTURE.md section 5 ("Protecting the system from the agents it serves") says the CLI admin
/// verbs that delete or bulk-change, naming <c>promote-all</c> by name, make an automatic backup first.
/// Slice 11b set this pattern for <see cref="ForgetProjectTool"/> (VACUUM INTO, reusing Store's
/// <see cref="BackupTool"/>); this is the second verb to get it.
/// </summary>
public sealed class PromoteAllTool : ITool
{
    public string Name => "promote-all";
    public string CliVerb => "promote-all";
    public string? McpName => null;
    public string Help => "promote-all --symbol <s>              replace a symbol's edges with its reviewed candidates";

    public string Execute(SqliteConnection connection, string root, string symbol)
    {
        List<int> ids = [];
        using (SqliteCommand c = connection.CreateCommand())
        {
            c.CommandText = "SELECT id FROM edge_candidates WHERE symbol=$s AND status='pending' ORDER BY id";
            c.Parameters.AddWithValue("$s", symbol);
            using SqliteDataReader r = c.ExecuteReader();
            while (r.Read()) ids.Add(r.GetInt32(0));
        }
        if (ids.Count == 0) return $"no pending candidates for '{symbol}'.";

        new BackupTool().Execute(connection, root, null);

        List<(string project, string file, int line, string usage, string contract)> old = [];
        using (SqliteCommand c = connection.CreateCommand())
        {
            c.CommandText = "SELECT project,file,line,usage,contract FROM edges WHERE symbol=$s";
            c.Parameters.AddWithValue("$s", symbol);
            using SqliteDataReader r = c.ExecuteReader();
            while (r.Read()) old.Add((r.GetString(0), r.GetString(1), r.GetInt32(2), r.GetString(3), r.GetString(4)));
        }

        using (SqliteCommand begin = connection.CreateCommand()) { begin.CommandText = "BEGIN"; begin.ExecuteNonQuery(); }
        foreach ((string project, string file, int line, string usage, string contract) in old)
            MutationLog.Append(connection, "edge", $"{symbol}|{project}|{file}|{line}", "delete", $"{contract}:{usage}", null, "promote-all-replace");
        using (SqliteCommand del = connection.CreateCommand())
        {
            del.CommandText = "DELETE FROM edges WHERE symbol=$s";
            del.Parameters.AddWithValue("$s", symbol);
            del.ExecuteNonQuery();
        }
        using (SqliteCommand commit = connection.CreateCommand()) { commit.CommandText = "COMMIT"; commit.ExecuteNonQuery(); }

        // PromoteCandidate (PromoteTool.Execute) prints one line per candidate on the CLI oracle
        // (grimora.cs:3023, `foreach (int id in ids) PromoteCandidate(id);`), before the summary line.
        PromoteTool promote = new();
        StringBuilder perCandidate = new();
        foreach (int id in ids) perCandidate.AppendLine(promote.Execute(connection, id));

        return $"{perCandidate}promoted {ids.Count} candidate(s) for '{symbol}' (replaced {old.Count} prior edge(s)).";
    }
}
