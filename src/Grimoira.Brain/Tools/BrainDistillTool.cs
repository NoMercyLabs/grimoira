using System.Security.Cryptography;
using System.Text;
using Grimoira.Brain.Data;
using Grimoira.Store.Tools;
using Microsoft.Data.Sqlite;

namespace Grimoira.Brain.Tools;

/// <summary>
/// PHASE A — deterministic, idempotent, reversible distillation of the existing channels into the graph.
/// memory -&gt; rule nodes (gloss=hook kept signal; body dropped to distill_log but reachable via ref);
/// memory.links -&gt; related triples (resolved tokens only); facts -&gt; fact nodes; edges -&gt; symbol nodes +
/// consumes triples (+ per-site refs). Nothing physically deleted; the source channels stay intact.
/// Copied verbatim from grimoira.cs's <c>BrainDistill</c> (grimoira.cs:2077-2151), writing through
/// <see cref="BrainWriters"/> and <see cref="BrainProjects"/> the same way <c>brain learn</c> does.
/// RESTRUCTURE.md section 5 says a delete/bulk-change verb backs up first (design checklist; as
/// forget-project) — distill bulk-writes many nodes/triples, so it takes the same
/// <see cref="BackupTool"/> snapshot first.
/// </summary>
public sealed class BrainDistillTool : ITool
{
    public string Name => "brain distill";
    public string CliVerb => "brain distill";
    public string? McpName => null;
    public string Help => "brain distill   fold memory/facts/edges channels into the graph (idempotent)";

    public string Execute(SqliteConnection connection, string root)
    {
        new BackupTool().Execute(connection, root, null);

        List<(string k, string title, string hook, string body, string links, long hard)> mems = [];
        using (SqliteCommand c = connection.CreateCommand())
        {
            c.CommandText = "SELECT k,title,hook,body,links,hard FROM memory";
            using SqliteDataReader r = c.ExecuteReader();
            while (r.Read())
                mems.Add((r.GetString(0), r.IsDBNull(1) ? "" : r.GetString(1), r.IsDBNull(2) ? "" : r.GetString(2),
                    r.IsDBNull(3) ? "" : r.GetString(3), r.IsDBNull(4) ? "" : r.GetString(4), r.GetInt64(5)));
        }
        List<(string k, string cat, string val)> facts = [];
        using (SqliteCommand c = connection.CreateCommand())
        {
            c.CommandText = "SELECT k,category,value FROM facts";
            using SqliteDataReader r = c.ExecuteReader();
            while (r.Read()) facts.Add((r.GetString(0), r.IsDBNull(1) ? "" : r.GetString(1), r.IsDBNull(2) ? "" : r.GetString(2)));
        }
        List<(long id, string project, string contract, string symbol)> edges = [];
        using (SqliteCommand c = connection.CreateCommand())
        {
            c.CommandText = "SELECT id,project,contract,symbol FROM edges";
            using SqliteDataReader r = c.ExecuteReader();
            while (r.Read()) edges.Add((r.GetInt64(0), r.GetString(1), r.IsDBNull(2) ? "" : r.GetString(2), r.GetString(3)));
        }

        HashSet<string> memKeys = [.. mems.Select(m => m.k)];
        int memN = 0, factN = 0, symN = 0, relN = 0, unresolved = 0;

        foreach ((string k, string title, string hook, string body, _, long hard) in mems)
        {
            string nk = "rule:" + k;
            BrainWriters.AddNode(connection, nk, "rule", title.Length > 0 ? title : k, hook, "", hard == 1, "distill:memory");
            Run(connection, "INSERT OR IGNORE INTO ref(node_k,channel,payload_k,role) VALUES($n,'memory',$p,'rule')", ("$n", nk), ("$p", k));
            Run(connection, "INSERT OR IGNORE INTO distill_log(node_k,source_file,content_hash,dropped) VALUES($n,$sf,$h,$d)",
                ("$n", nk), ("$sf", "memory:" + k), ("$h", Hash(hook + "|" + body)), ("$d", body));
            memN++;
        }
        foreach ((string k, _, _, _, string links, _) in mems)
        {
            if (links.Length == 0) continue;
            string sk = "rule:" + k;
            foreach (string tok in links.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                string? target = ResolveMemToken(tok, memKeys);
                if (target is null)
                {
                    unresolved++;
                    Run(connection, "UPDATE distill_log SET unresolved = unresolved||$t||',' WHERE node_k=$n", ("$t", tok), ("$n", sk));
                    continue;
                }
                BrainWriters.AddTriple(connection, sk, "related", "rule:" + target, "", "distill:links", false, "distill:links");
                relN++;
            }
        }
        foreach ((string k, string cat, string val) in facts)
        {
            string nk = "fact:" + k;
            BrainWriters.AddNode(connection, nk, "fact", k, val.Length <= 120 ? val : val[..120], cat, false, "distill:facts");
            Run(connection, "INSERT OR IGNORE INTO ref(node_k,channel,payload_k,role) VALUES($n,'facts',$p,'value')", ("$n", nk), ("$p", k));
            factN++;
        }
        foreach (string ps in edges.Select(e => e.project).Distinct())
            BrainWriters.AddNode(connection, BrainProjects.Normalize(connection, ps), "project", ps, "", "", false, "distill:edges");
        foreach ((string contract, string symbol) in edges.Select(e => (e.contract, e.symbol)).Distinct())
        {
            BrainWriters.AddNode(connection, $"contract:{contract}.{symbol}", "symbol", symbol, "", "", false, "distill:edges");
            symN++;
        }
        foreach ((string project, string contract, string symbol) in edges.Select(e => (e.project, e.contract, e.symbol)).Distinct())
            BrainWriters.AddTriple(connection, BrainProjects.Normalize(connection, project), "consumes", $"contract:{contract}.{symbol}", "", "distill:edges", false, "distill:edges");
        foreach ((long id, _, string contract, string symbol) in edges)
            Run(connection, "INSERT OR IGNORE INTO ref(node_k,channel,payload_k,role) VALUES($n,'edges',$p,'site')", ("$n", $"contract:{contract}.{symbol}"), ("$p", id.ToString(System.Globalization.CultureInfo.InvariantCulture)));

        using (SqliteCommand rebuildNode = connection.CreateCommand()) { rebuildNode.CommandText = "INSERT INTO node_fts(node_fts) VALUES('rebuild')"; rebuildNode.ExecuteNonQuery(); }
        using (SqliteCommand rebuildSlot = connection.CreateCommand()) { rebuildSlot.CommandText = "INSERT INTO slot_fts(slot_fts) VALUES('rebuild')"; rebuildSlot.ExecuteNonQuery(); }

        return $"distilled: {memN} rule nodes, {factN} fact nodes, {symN} symbol nodes, {relN} related links ({unresolved} unresolved -> distill_log). source channels untouched.";
    }

    /// <summary>Copied verbatim from grimoira.cs's <c>Hash</c> (grimoira.cs:1678-1682).</summary>
    private static string Hash(string text)
    {
        byte[] b = SHA1.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(b);
    }

    /// <summary>Resolve a memory.links token to an actual memory.k: exact, then '-'-&gt;'_', then probe the
    /// type prefixes. Copied verbatim from grimoira.cs's <c>ResolveMemToken</c> (grimoira.cs:1685-1696).</summary>
    private static string? ResolveMemToken(string tok, HashSet<string> keys)
    {
        if (keys.Contains(tok)) return tok;
        string u = tok.Replace('-', '_');
        if (keys.Contains(u)) return u;
        foreach (string pre in new[] { "feedback_", "reference_", "project_", "user_" })
        {
            if (keys.Contains(pre + u)) return pre + u;
            if (keys.Contains(pre + tok)) return pre + tok;
        }
        return null;
    }

    private static void Run(SqliteConnection connection, string sql, params (string name, object? value)[] parameters)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        foreach ((string name, object? value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        command.ExecuteNonQuery();
    }
}
