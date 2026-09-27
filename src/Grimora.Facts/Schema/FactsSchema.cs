using Grimora.Store.Schema;

namespace Grimora.Facts.Schema;

/// <summary>
/// Facts' 3 tables (RESTRUCTURE.md section 3.1): <c>facts</c>, <c>todos</c>, <c>findings</c> (plus
/// <c>facts_fts</c>). Every statement is copied verbatim from today's grimora.cs Init() so an empty store
/// built from this schema is byte-identical, table for table, to one built by the old code.
///
/// The <c>facts</c> table folds in the <c>provenance</c> column directly, rather than replaying
/// grimora.cs's separate <c>TryExec("ALTER TABLE facts ADD COLUMN provenance ...")</c> (grimora.cs:379):
/// on an empty store, SQLite's own ALTER TABLE rewrites the stored <c>sqlite_master.sql</c> to the same
/// text a table declared with the column from the start would have (verified byte-for-byte), and folding
/// it in makes the statement safe to run twice — StoreSchema already uses the same technique for
/// <c>usage.verified_at</c>.
/// </summary>
public sealed class FactsSchema : ISchemaProvider
{
    public string Name => "Facts";

    public IReadOnlyList<string> Statements { get; } =
    [
        // grimora.cs:375 + :379 (provenance column folded in, see class remarks)
        "CREATE TABLE IF NOT EXISTS facts(k TEXT PRIMARY KEY, term TEXT, aliases TEXT, category TEXT, value TEXT, source TEXT, notes TEXT, provenance TEXT NOT NULL DEFAULT 'unverified');",
        // grimora.cs:381
        "CREATE VIRTUAL TABLE IF NOT EXISTS facts_fts USING fts5(k UNINDEXED, term, aliases, category, value, notes);",
        // grimora.cs:383
        "CREATE TABLE IF NOT EXISTS todos(id INTEGER PRIMARY KEY, ts TEXT, title TEXT, status TEXT, why TEXT);",
        // grimora.cs:384
        "CREATE TABLE IF NOT EXISTS findings(id INTEGER PRIMARY KEY, ts TEXT, title TEXT, detail TEXT, source TEXT, status TEXT);",
    ];
}
