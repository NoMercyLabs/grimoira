using Grimora.Store.Tools;
using Microsoft.Data.Sqlite;

namespace Grimora.Brain.Tools;

/// <summary>
/// Health snapshot of the brain graph — node/triple/slot/ref counts, usage reinforcement, seam/codekind
/// coverage, freshness, and open gaps. CLI-only sub-verb (RESTRUCTURE.md section 2.2 lists no MCP
/// counterpart for <c>brain stats</c>). Copied verbatim from grimora.cs's <c>BrainStats</c> (grimora.cs:1580).
/// </summary>
public sealed class BrainStatsTool : ITool
{
    public string Name => "brain stats";
    public string CliVerb => "brain stats";
    public string? McpName => null;
    public string Help =>
        "brain stats                            health snapshot: node/triple/slot/ref counts, usage " +
        "reinforcement, seam/codekind coverage, freshness, open gaps. No args.";

    public string Execute(SqliteConnection connection)
    {
        System.Text.StringBuilder sb = new();
        sb.AppendLine($"  nodes   {ScalarLong(connection, "SELECT count(*) FROM node_now")}  ({ScalarLong(connection, "SELECT count(*) FROM node_now WHERE hard=1")} hard)");
        sb.AppendLine($"  triples {ScalarLong(connection, "SELECT count(*) FROM triple_now")}");
        sb.AppendLine($"  slots   {ScalarLong(connection, "SELECT count(*) FROM slot_now")}");
        sb.AppendLine($"  refs    {ScalarLong(connection, "SELECT count(*) FROM ref")}");
        sb.AppendLine($"  usage   {ScalarLong(connection, "SELECT count(*) FROM usage")} reinforced; top: {ScalarText(connection, "SELECT group_concat(node_k||'×'||hits,', ') FROM (SELECT node_k,hits FROM usage ORDER BY hits DESC LIMIT 3)") ?? "(none yet)"}");
        sb.AppendLine($"  coverage  seams {ScalarLong(connection, "SELECT count(*) FROM node_now n WHERE kind='seam' AND EXISTS(SELECT 1 FROM triple_now WHERE o=n.k AND p='consumes')")}/{ScalarLong(connection, "SELECT count(*) FROM node_now WHERE kind='seam'")} consumed, codekinds {ScalarLong(connection, "SELECT count(*) FROM node_now n WHERE kind='codekind' AND EXISTS(SELECT 1 FROM slot_now WHERE frame_k=n.k)")}/{ScalarLong(connection, "SELECT count(*) FROM node_now WHERE kind='codekind'")} placed");
        sb.AppendLine($"  fresh   {ScalarLong(connection, "SELECT count(*) FROM node_now n JOIN usage u ON u.node_k=n.k WHERE u.verified_at IS NOT NULL")}/{ScalarLong(connection, "SELECT count(*) FROM node_now")} confirmed against code");
        sb.AppendLine($"  gaps    {ScalarLong(connection, "SELECT count(*) FROM gaps WHERE status='open'")} open / {ScalarLong(connection, "SELECT count(*) FROM gaps")} total (unanswerable lookups awaiting a learn)");
        using SqliteCommand c = connection.CreateCommand();
        c.CommandText = "SELECT kind, count(*) FROM node_now GROUP BY kind ORDER BY 2 DESC";
        using SqliteDataReader r = c.ExecuteReader();
        while (r.Read()) sb.AppendLine($"    {r.GetString(0),-12} {r.GetInt32(1)}");
        return sb.ToString();
    }

    private static long ScalarLong(SqliteConnection connection, string sql)
    {
        using SqliteCommand c = connection.CreateCommand();
        c.CommandText = sql;
        return (long)(c.ExecuteScalar() ?? 0L);
    }

    private static string? ScalarText(SqliteConnection connection, string sql)
    {
        using SqliteCommand c = connection.CreateCommand();
        c.CommandText = sql;
        object? v = c.ExecuteScalar();
        return v is null or DBNull ? null : Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture);
    }
}
