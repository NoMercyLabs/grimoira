using Aitm.Brain.Data;
using Aitm.Store.Tools;
using Microsoft.Data.Sqlite;

namespace Aitm.Brain.Tools;

/// <summary>
/// One-node 360 view: the node itself, its outgoing and incoming edges, its slots, and its channel refs —
/// everything the brain knows about a single thing, in one read. CLI-only sub-verb (RESTRUCTURE.md
/// section 2.2 lists no MCP counterpart for <c>brain why</c>). Copied verbatim from aitm.cs's
/// <c>BrainWhy</c> (aitm.cs:1838).
/// </summary>
public sealed class BrainWhyTool : ITool
{
    public string Name => "brain why";
    public string CliVerb => "brain why";
    public string? McpName => null;
    public string Help =>
        "brain why <node-key>                   one-node 360 view: the node, its in/out edges, its " +
        "slots, and its channel refs — everything the brain knows about one thing.";

    public string Execute(SqliteConnection connection, string k)
    {
        if (ScalarLong(connection, "SELECT count(*) FROM node_now WHERE k=$k", k) == 0)
            return $"no live node '{k}'.";

        System.Text.StringBuilder sb = new();
        void Q(string label, string sql)
        {
            sb.AppendLine($"  {label}:");
            using SqliteCommand c = connection.CreateCommand();
            c.CommandText = sql;
            c.Parameters.AddWithValue("$k", k);
            (string output, _) = BrainCliRows.RunReader(c);
            sb.Append(output);
        }
        Q("self", "SELECT kind,label,gloss,COALESCE(scheme,''),'hard='||hard FROM node_now WHERE k=$k");
        Q("out (this -> X)", "SELECT p,o,because FROM triple_now WHERE s=$k ORDER BY p");
        Q("in (X -> this)", "SELECT s,p FROM triple_now WHERE o=$k AND o_is_literal=0 ORDER BY p");
        Q("slots", "SELECT name,value FROM slot_now WHERE frame_k=$k ORDER BY name");
        Q("refs", "SELECT channel,payload_k FROM ref WHERE node_k=$k");
        return sb.ToString();
    }

    private static long ScalarLong(SqliteConnection connection, string sql, string k)
    {
        using SqliteCommand c = connection.CreateCommand();
        c.CommandText = sql;
        c.Parameters.AddWithValue("$k", k);
        return (long)(c.ExecuteScalar() ?? 0L);
    }
}
