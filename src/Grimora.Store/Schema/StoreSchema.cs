namespace Grimora.Store.Schema;

/// <summary>
/// Store's own 4 tables (RESTRUCTURE.md section 3.1): <c>meta</c>, <c>mutations</c>, <c>gaps</c>,
/// <c>usage</c>. Every statement is copied verbatim from today's grimora.cs (Init() and InitBrain()) so
/// an empty store built from this schema is byte-identical, table for table, to one built by the old
/// code — the parity a slice-3a test pins down.
/// </summary>
public sealed class StoreSchema : ISchemaProvider
{
    public string Name => "Store";

    public IReadOnlyList<string> Statements { get; } =
    [
        // grimora.cs:380
        "CREATE TABLE IF NOT EXISTS mutations(id INTEGER PRIMARY KEY, ts TEXT, op TEXT, kind TEXT, k TEXT, before TEXT, after TEXT, why TEXT);",
        // grimora.cs:405
        "CREATE TABLE IF NOT EXISTS gaps(id INTEGER PRIMARY KEY, query TEXT NOT NULL UNIQUE, tool TEXT NOT NULL, misses INTEGER NOT NULL DEFAULT 1, first_ts TEXT NOT NULL, last_ts TEXT NOT NULL, status TEXT NOT NULL DEFAULT 'open');",
        // grimora.cs:406
        "CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY, value TEXT);",
        // grimora.cs:585 (note the space before the column list — copied as-is for sqlite_master parity)
        "CREATE TABLE IF NOT EXISTS usage (node_k TEXT PRIMARY KEY, hits INTEGER NOT NULL DEFAULT 0, last_used TEXT, verified_at TEXT);",
    ];
}
