using Aitm.Store.Tools;
using Microsoft.Data.Sqlite;

namespace Aitm.Brain.Tools;

/// <summary>
/// Read-only integrity report: surfaces the knowledge-hygiene issues worth fixing (isolated nodes,
/// duplicate labels, incomplete placement frames, seams with no consumer, and any dead-reference triples).
/// CLI-only sub-verb (RESTRUCTURE.md section 2.2 lists no MCP counterpart for <c>brain audit</c>). Copied
/// verbatim from aitm.cs's <c>BrainAudit</c> (aitm.cs:1858), except the header line: aitm.cs prints the
/// live instance name (a global only the running process knows); this tool takes it as a parameter.
/// </summary>
public sealed class BrainAuditTool : ITool
{
    public string Name => "brain audit";
    public string CliVerb => "brain audit";
    public string? McpName => null;
    public string Help =>
        "brain audit                            read-only integrity report: orphan nodes, duplicate " +
        "labels, incomplete placement, unconsumed seams, dead-reference triples, contradictions. No args.";

    public string Execute(SqliteConnection connection, string instance)
    {
        System.Text.StringBuilder sb = new();
        sb.AppendLine($"brain audit ({instance}):");
        void Section(string label, string sql)
        {
            long n = ScalarLong(connection, $"SELECT count(*) FROM ({sql})");
            sb.AppendLine($"  {label,-36} {n}");
            if (n is > 0 and <= 12)
            {
                using SqliteCommand c = connection.CreateCommand();
                c.CommandText = sql + " LIMIT 12";
                using SqliteDataReader r = c.ExecuteReader();
                while (r.Read()) sb.AppendLine($"      - {r.GetString(0)}");
            }
        }
        Section("orphan nodes (no edge/slot/ref)",
            "SELECT k FROM node_now n WHERE NOT EXISTS(SELECT 1 FROM triple_now t WHERE t.s=n.k OR (t.o=n.k AND t.o_is_literal=0)) AND NOT EXISTS(SELECT 1 FROM slot_now s WHERE s.frame_k=n.k) AND NOT EXISTS(SELECT 1 FROM ref r WHERE r.node_k=n.k)");
        Section("duplicate labels",
            "SELECT label||' ('||count(*)||')' FROM node_now GROUP BY lower(label) HAVING count(*)>1");
        Section("codekinds with no placement slots",
            "SELECT k FROM node_now WHERE kind='codekind' AND NOT EXISTS(SELECT 1 FROM slot_now s WHERE s.frame_k=node_now.k)");
        Section("codekinds with no belongs_in",
            "SELECT k FROM node_now WHERE kind='codekind' AND NOT EXISTS(SELECT 1 FROM triple_now t WHERE t.s=node_now.k AND t.p='belongs_in')");
        Section("seams nobody consumes",
            "SELECT k FROM node_now WHERE kind='seam' AND NOT EXISTS(SELECT 1 FROM triple_now t WHERE t.o=k AND t.p='consumes')");
        Section("triples with a dead subject",
            "SELECT s||' '||p||' '||o FROM triple_now t WHERE NOT EXISTS(SELECT 1 FROM node_now n WHERE n.k=t.s)");
        Section("link-triples with a dead object",
            "SELECT s||' '||p||' '||o FROM triple_now t WHERE o_is_literal=0 AND NOT EXISTS(SELECT 1 FROM node_now n WHERE n.k=t.o)");
        Section("contradictions (conflicting predicates)",
            "SELECT DISTINCT t1.s||' ['||min(t1.p,t2.p)||' vs '||max(t1.p,t2.p)||'] '||t1.o FROM triple_now t1 JOIN pred_vocab v ON v.p=t1.p AND v.conflicts IS NOT NULL JOIN triple_now t2 ON t2.s=t1.s AND t2.o=t1.o AND t2.p=v.conflicts");
        Section("nodes with empty gloss",
            "SELECT k FROM node_now WHERE (gloss IS NULL OR gloss='') AND kind NOT IN ('codekind')");
        Section("nodes with no scheme",
            "SELECT k FROM node_now WHERE scheme IS NULL OR scheme=''");
        Section("scrambled writes (paragraph label/kind)",
            "SELECT k FROM node_now WHERE length(label) > 90 OR length(kind) > 30 OR kind LIKE '% %'");
        return sb.ToString();
    }

    private static long ScalarLong(SqliteConnection connection, string sql)
    {
        using SqliteCommand c = connection.CreateCommand();
        c.CommandText = sql;
        return (long)(c.ExecuteScalar() ?? 0L);
    }
}
