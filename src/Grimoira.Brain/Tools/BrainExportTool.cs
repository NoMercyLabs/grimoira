using System.Globalization;
using System.Text;
using Grimoira.Store.Tools;
using Microsoft.Data.Sqlite;

namespace Grimoira.Brain.Tools;

/// <summary>
/// Diffable, re-importable snapshot of the live graph in the learn-batch format (nodes, then triples,
/// then slots — the order learn-batch needs). <c>'|'</c> inside a value is sanitized so a round trip
/// never corrupts. Copied verbatim from grimoira.cs's <c>BrainExport</c> (grimoira.cs:1995). CLI-only sub-verb
/// (no MCP counterpart, RESTRUCTURE.md section 2.2).
/// </summary>
public sealed class BrainExportTool : ITool
{
    public string Name => "brain export";
    public string CliVerb => "brain export";
    public string? McpName => null;
    public string Help =>
        "brain export [--to <path>]             re-importable snapshot of the live graph in the " +
        "learn-batch format, default <root>/brain-export.txt.";

    public string ExecuteCli(SqliteConnection connection, string to)
    {
        static string San(string s) => s.Replace("|", "/").Replace("\r", " ").Replace("\n", " ");
        StringBuilder sb = new();
        sb.AppendLine("# brain graph export — re-import: grimoira brain learn-batch --from <this-file>");
        using (SqliteCommand c = connection.CreateCommand())
        {
            c.CommandText = "SELECT k,kind,label,gloss,hard,COALESCE(scheme,'') FROM node_now ORDER BY kind,k";
            using SqliteDataReader r = c.ExecuteReader();
            while (r.Read())
                sb.AppendLine($"node | {San(r.GetString(0))} | {San(r.GetString(1))} | {San(r.GetString(2))} | {San(r.GetString(3))} | {r.GetInt32(4)} | {San(r.GetString(5))}");
        }
        using (SqliteCommand c = connection.CreateCommand())
        {
            c.CommandText = "SELECT s,p,o,COALESCE(because,'') FROM triple_now ORDER BY s,p,o";
            using SqliteDataReader r = c.ExecuteReader();
            while (r.Read())
                sb.AppendLine($"triple | {San(r.GetString(0))} | {San(r.GetString(1))} | {San(r.GetString(2))} | {San(r.GetString(3))}");
        }
        using (SqliteCommand c = connection.CreateCommand())
        {
            c.CommandText = "SELECT frame_k,name,value,COALESCE(facet,'text'),multi FROM slot_now ORDER BY frame_k,name,value";
            using SqliteDataReader r = c.ExecuteReader();
            while (r.Read())
                sb.AppendLine($"slot | {San(r.GetString(0))} | {San(r.GetString(1))} | {San(r.GetString(2))} | {San(r.GetString(3))} | {r.GetInt32(4)}");
        }
        File.WriteAllText(to, sb.ToString());
        return $"exported {ScalarLong(connection, "SELECT count(*) FROM node_now")} nodes / {ScalarLong(connection, "SELECT count(*) FROM triple_now")} triples / {ScalarLong(connection, "SELECT count(*) FROM slot_now")} slots -> {to}";
    }

    private static long ScalarLong(SqliteConnection connection, string sql)
    {
        using SqliteCommand c = connection.CreateCommand();
        c.CommandText = sql;
        return Convert.ToInt64(c.ExecuteScalar(), CultureInfo.InvariantCulture);
    }
}
