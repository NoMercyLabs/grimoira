using Grimoira.Store.Schema;

namespace Grimoira.Docs.Schema;

/// <summary>
/// Docs' 1 table (RESTRUCTURE.md section 3.1): <c>docs</c> (plus <c>docs_fts</c>). Every statement is
/// copied verbatim from today's grimoira.cs Init(). The <c>terms</c> column already sits in the base
/// <c>CREATE TABLE</c> (grimoira.cs:394), so grimoira.cs's follow-up <c>TryExec("ALTER TABLE docs ADD COLUMN
/// terms TEXT")</c> (grimoira.cs:395) is a genuine no-op on an empty store — a migration for stores created
/// before the column existed — and is not replayed here.
/// </summary>
public sealed class DocsSchema : ISchemaProvider
{
    public string Name => "Docs";

    public IReadOnlyList<string> Statements { get; } =
    [
        // grimoira.cs:394
        "CREATE TABLE IF NOT EXISTS docs(k TEXT PRIMARY KEY, path TEXT, title TEXT, category TEXT, content TEXT, terms TEXT);",
        // grimoira.cs:396
        "CREATE VIRTUAL TABLE IF NOT EXISTS docs_fts USING fts5(k UNINDEXED, title, content);",
    ];
}
