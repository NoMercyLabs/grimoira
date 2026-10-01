namespace Grimoira.Store.Schema;

/// <summary>
/// One project's own schema: the DDL statements for the tables it owns. Store's <see cref="SchemaRunner"/>
/// applies every registered provider without ever knowing what any of them creates, so the dependency
/// direction only ever points down to Store (RESTRUCTURE.md section 1: "May depend on: nothing in
/// Grimoira" for Store, "May depend on: Store" for every channel above it).
/// </summary>
public interface ISchemaProvider
{
    /// <summary>A short, stable name for this project's schema (the meta key "schema:&lt;Name&gt;" once
    /// the runner tracks per-project migration steps in a later slice).</summary>
    string Name { get; }

    /// <summary>
    /// The statements that create this project's tables, indexes, triggers and views. Every statement
    /// must be safe to run against a store that already has them — RESTRUCTURE.md section 3.2 rule 3:
    /// additive and idempotent while an old binary can still open the same file.
    /// </summary>
    IReadOnlyList<string> Statements { get; }
}
