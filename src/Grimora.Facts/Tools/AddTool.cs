using Grimora.Facts.Data;
using Grimora.Store.Data;
using Grimora.Store.Tools;
using Microsoft.Data.Sqlite;

namespace Grimora.Facts.Tools;

/// <summary>
/// Records or updates one verified fact. Copied verbatim from grimora.cs's <c>AddCmd</c> (grimora.cs:2417) and
/// the <c>UpsertFact</c> helper it calls (grimora.cs:616) — the same upsert-plus-cold-log shape
/// <c>ImportTool</c> (Grimora.Store) already carries for its own bulk path.
/// </summary>
public sealed class AddTool : ITool
{
    public string Name => "add";
    public string CliVerb => "add";
    public string? McpName => null;
    public string Help =>
        "add --term <t> [--value <v>] [--category <c>] [--aliases <json>] [--source <s>] [--notes <n>] " +
        "[--provenance stated|extracted|inferred|unverified]   record or update a verified fact";

    public string Execute(SqliteConnection connection, string term, string aliases, string category,
        string value, string source, string notes, string provenance, string why = "manual")
    {
        string prov = provenance.ToLowerInvariant();
        if (prov is not ("stated" or "extracted" or "inferred" or "unverified"))
            throw new ArgumentException("--provenance must be stated, extracted, inferred, or unverified");

        // Secrets in outputs (docs/RESTRUCTURE.md): value/notes are free text, same shape as a chat message
        // IndexChatTool already scrubs.
        (value, _) = SecretScrubber.Redact(value);
        (notes, _) = SecretScrubber.Redact(notes);

        using (SqliteCommand begin = connection.CreateCommand()) { begin.CommandText = "BEGIN"; begin.ExecuteNonQuery(); }
        UpsertFact(connection, term, term, aliases, category, value, source, notes, why);
        using (SqliteCommand update = connection.CreateCommand())
        {
            update.CommandText = "UPDATE facts SET provenance=$p WHERE k=$k";
            update.Parameters.AddWithValue("$p", prov);
            update.Parameters.AddWithValue("$k", term);
            update.ExecuteNonQuery();
        }
        using (SqliteCommand commit = connection.CreateCommand()) { commit.CommandText = "COMMIT"; commit.ExecuteNonQuery(); }

        return $"added/updated '{term}' [{prov}] (logged to mutations).";
    }

    private static void UpsertFact(SqliteConnection connection, string k, string term, string aliases,
        string category, string value, string source, string notes, string why)
    {
        string? before = FactRecord.ReadJson(connection, k);
        Run(connection, "INSERT INTO facts(k,term,aliases,category,value,source,notes) VALUES($k,$t,$al,$c,$v,$s,$n) " +
            "ON CONFLICT(k) DO UPDATE SET term=$t,aliases=$al,category=$c,value=$v,source=$s,notes=$n",
            ("$k", k), ("$t", term), ("$al", aliases), ("$c", category), ("$v", value), ("$s", source), ("$n", notes));
        Run(connection, "DELETE FROM facts_fts WHERE k=$k", ("$k", k));
        Run(connection, "INSERT INTO facts_fts(k,term,aliases,category,value,notes) VALUES($k,$t,$al,$c,$v,$n)",
            ("$k", k), ("$t", term), ("$al", aliases), ("$c", category), ("$v", value), ("$n", notes));
        string after = FactRecord.ReadJson(connection, k)!;
        if (before != after)
            MutationLog.Append(connection, "fact", k, before is null ? "insert" : "update", before, after, why);
    }

    private static void Run(SqliteConnection connection, string sql, params (string name, string value)[] parameters)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        foreach ((string name, string value) in parameters) command.Parameters.AddWithValue(name, value);
        command.ExecuteNonQuery();
    }
}
