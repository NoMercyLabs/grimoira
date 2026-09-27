using Grimora.Store.Tools;
using Grimora.Store.Data;
using Microsoft.Data.Sqlite;

namespace Grimora.Graph.Tools;

/// <summary>
/// Promotes one reviewed candidate into the curated graph, logging the insert to the cold mutation
/// log through the same path <c>seed-edges</c> uses. Copied verbatim from grimora.cs's
/// <c>PromoteCandidate</c> (grimora.cs:2969-2994). Not required to have a new pinned test by the slice 12
/// card ("all except promote and seed-edges (selftest has them)"), but ported here so
/// <see cref="PromoteAllTool"/> can call it in-process rather than shelling back out to the CLI.
/// </summary>
public sealed class PromoteTool : ITool
{
    public string Name => "promote";
    public string CliVerb => "promote";
    public string? McpName => null;
    public string Help => "promote <id>                          move a reviewed candidate into the curated graph";

    public string Execute(SqliteConnection connection, int id)
    {
        string sym, contract, proj, file, usage;
        int line, hard;
        using (SqliteCommand c = connection.CreateCommand())
        {
            c.CommandText = "SELECT symbol,contract,project,file,line,usage,hardcoded FROM edge_candidates WHERE id=$i AND status='pending'";
            c.Parameters.AddWithValue("$i", id);
            using SqliteDataReader r = c.ExecuteReader();
            if (!r.Read()) return $"no pending candidate #{id}.";
            sym = r.GetString(0); contract = r.GetString(1); proj = r.GetString(2);
            file = r.GetString(3); line = r.GetInt32(4); usage = r.GetString(5); hard = r.GetInt32(6);
        }

        long exists;
        using (SqliteCommand count = connection.CreateCommand())
        {
            count.CommandText = "SELECT count(*) FROM edges WHERE symbol=$s AND project=$p AND file=$f AND line=$l";
            count.Parameters.AddWithValue("$s", sym);
            count.Parameters.AddWithValue("$p", proj);
            count.Parameters.AddWithValue("$f", file);
            count.Parameters.AddWithValue("$l", line);
            exists = (long)(count.ExecuteScalar() ?? 0L);
        }

        // file_rel (RESTRUCTURE.md slice 31b): same column-exists guard GraphFileRelSchema.HasColumn
        // already gives every reader, so a v3 store (no column yet) inserts exactly as before.
        bool hasFileRel = Schema.GraphFileRelSchema.HasColumn(connection, "edges", "file_rel");
        string? fileRel = null;
        if (hasFileRel && Schema.GraphFileRelSchema.LoadProjectRoots(connection).TryGetValue(proj, out string? root))
            fileRel = Schema.GraphFileRelSchema.ToRelative(root, file);

        using (SqliteCommand begin = connection.CreateCommand()) { begin.CommandText = "BEGIN"; begin.ExecuteNonQuery(); }
        if (exists == 0)
        {
            using (SqliteCommand insert = connection.CreateCommand())
            {
                insert.CommandText = hasFileRel
                    ? "INSERT INTO edges(symbol,contract,project,file,line,usage,hardcoded,file_rel) VALUES($s,$c,$p,$f,$l,$u,$h,$fr)"
                    : "INSERT INTO edges(symbol,contract,project,file,line,usage,hardcoded) VALUES($s,$c,$p,$f,$l,$u,$h)";
                insert.Parameters.AddWithValue("$s", sym);
                insert.Parameters.AddWithValue("$c", contract);
                insert.Parameters.AddWithValue("$p", proj);
                insert.Parameters.AddWithValue("$f", file);
                insert.Parameters.AddWithValue("$l", line);
                insert.Parameters.AddWithValue("$u", usage);
                insert.Parameters.AddWithValue("$h", hard);
                if (hasFileRel) insert.Parameters.AddWithValue("$fr", (object?)fileRel ?? DBNull.Value);
                insert.ExecuteNonQuery();
            }
            MutationLog.Append(connection, "edge", $"{sym}|{proj}|{file}|{line}", "insert", null, $"{contract}:{usage}", $"promote:#{id}");
        }
        using (SqliteCommand update = connection.CreateCommand())
        {
            update.CommandText = "UPDATE edge_candidates SET status='promoted' WHERE id=$i";
            update.Parameters.AddWithValue("$i", id);
            update.ExecuteNonQuery();
        }
        using (SqliteCommand commit = connection.CreateCommand()) { commit.CommandText = "COMMIT"; commit.ExecuteNonQuery(); }

        return exists == 0
            ? $"promoted #{id}: {sym} -> {proj} {file}:{line} (now in the graph)."
            : $"#{id} already in graph; marked promoted.";
    }
}
