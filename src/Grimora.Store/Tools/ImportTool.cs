using Grimora.Store.Data;
using Microsoft.Data.Sqlite;

namespace Grimora.Store.Tools;

/// <summary>
/// Merges another grimora.db's facts into this one. Copied verbatim from grimora.cs's <c>Import</c>
/// (grimora.cs:710-737) and the <c>UpsertFact</c>/<c>ReadFactJson</c> helpers it calls (grimora.cs:602-629).
/// <c>import</c> sits in Grimora.Store per RESTRUCTURE.md section 2.1 even though it writes the
/// <c>facts</c> table Facts will own its own tools for from slice 5 — this mirrors the merge grimora.cs
/// already does today, verbatim, not a new cross-project dependency.
/// </summary>
public sealed class ImportTool : ITool
{
    public string Name => "import";
    public string CliVerb => "import";
    public string? McpName => null;
    public string Help => "import --from <db>                  merge another grimora.db into this one";

    public string Execute(SqliteConnection connection, string fromDbPath, string dbPath)
    {
        using SqliteConnection source = new($"Data Source={fromDbPath};Mode=ReadOnly");
        source.Open();
        using SqliteCommand select = source.CreateCommand();
        select.CommandText = "SELECT term,aliases,category,value,source,notes FROM facts";
        using SqliteDataReader reader = select.ExecuteReader();

        int imported = 0;
        Exec(connection, "BEGIN");
        while (reader.Read())
        {
            string term = reader.GetString(0);
            UpsertFact(
                connection,
                k: term,
                term: term,
                aliases: reader.IsDBNull(1) ? "[]" : reader.GetString(1),
                category: reader.GetString(2),
                value: reader.GetString(3),
                source: reader.IsDBNull(4) ? "" : reader.GetString(4),
                notes: reader.IsDBNull(5) ? "" : reader.GetString(5),
                why: "import:" + Path.GetFileName(fromDbPath));
            imported++;
        }
        Exec(connection, "COMMIT");

        long facts = Scalar(connection, "SELECT count(*) FROM facts");
        long mutations = Scalar(connection, "SELECT count(*) FROM mutations");
        return $"imported {imported} rows -> {facts} current facts, {mutations} mutations logged. ({dbPath})";
    }

    private static void UpsertFact(SqliteConnection connection, string k, string term, string aliases, string category, string value, string source, string notes, string why)
    {
        string? before = ReadFactJson(connection, k);
        Run(connection, "INSERT INTO facts(k,term,aliases,category,value,source,notes) VALUES($k,$t,$al,$c,$v,$s,$n) " +
            "ON CONFLICT(k) DO UPDATE SET term=$t,aliases=$al,category=$c,value=$v,source=$s,notes=$n",
            ("$k", k), ("$t", term), ("$al", aliases), ("$c", category), ("$v", value), ("$s", source), ("$n", notes));
        Run(connection, "DELETE FROM facts_fts WHERE k=$k", ("$k", k));
        Run(connection, "INSERT INTO facts_fts(k,term,aliases,category,value,notes) VALUES($k,$t,$al,$c,$v,$n)",
            ("$k", k), ("$t", term), ("$al", aliases), ("$c", category), ("$v", value), ("$n", notes));
        string after = ReadFactJson(connection, k)!;
        if (before != after)
            MutationLog.Append(connection, "fact", k, before is null ? "insert" : "update", before, after, why);
    }

    private static string? ReadFactJson(SqliteConnection connection, string k)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT term,aliases,category,value,source,notes FROM facts WHERE k=$k";
        command.Parameters.AddWithValue("$k", k);
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        static string E(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r") + "\"";
        return "{" +
            $"\"term\":{E(reader.GetString(0))},\"aliases\":{E(reader.GetString(1))},\"category\":{E(reader.GetString(2))}," +
            $"\"value\":{E(reader.GetString(3))},\"source\":{E(reader.GetString(4))},\"notes\":{E(reader.GetString(5))}}}";
    }

    private static void Exec(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void Run(SqliteConnection connection, string sql, params (string name, string value)[] parameters)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        foreach ((string name, string value) in parameters) command.Parameters.AddWithValue(name, value);
        command.ExecuteNonQuery();
    }

    private static long Scalar(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)(command.ExecuteScalar() ?? 0L);
    }
}
