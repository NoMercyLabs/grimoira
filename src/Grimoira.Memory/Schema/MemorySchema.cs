using Grimoira.Store.Schema;

namespace Grimoira.Memory.Schema;

/// <summary>
/// Memory's 2 tables (RESTRUCTURE.md section 3.1): <c>memory</c>, <c>chat</c> (plus <c>memory_fts</c> and
/// <c>chat_fts</c>). Every statement is copied verbatim from today's grimoira.cs Init().
/// </summary>
public sealed class MemorySchema : ISchemaProvider
{
    public string Name => "Memory";

    public IReadOnlyList<string> Statements { get; } =
    [
        // grimoira.cs:386
        "CREATE TABLE IF NOT EXISTS chat(k TEXT PRIMARY KEY, session TEXT, ts TEXT, role TEXT, text TEXT);",
        // grimoira.cs:387
        "CREATE VIRTUAL TABLE IF NOT EXISTS chat_fts USING fts5(k UNINDEXED, text);",
        // grimoira.cs:400
        "CREATE TABLE IF NOT EXISTS memory(k TEXT PRIMARY KEY, type TEXT, title TEXT, hook TEXT, body TEXT, links TEXT, hard INTEGER);",
        // grimoira.cs:401
        "CREATE VIRTUAL TABLE IF NOT EXISTS memory_fts USING fts5(k UNINDEXED, title, hook, body);",
    ];
}
