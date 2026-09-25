using Aitm.Store.Schema;

namespace Aitm.Graph.Schema;

/// <summary>
/// Graph's 3 tables (RESTRUCTURE.md section 3.1): <c>edges</c>, <c>edge_candidates</c>, <c>projects</c>.
/// Every statement is copied verbatim from today's aitm.cs Init().
///
/// <c>edges_file_idx</c> is NOT included here. Section 3.1 asks for it to move here so Graph "creates
/// it once" instead of the duplicate lazy calls in aitm.cs (GraphBfs, aitm.cs:2562) and mcp.cs
/// (mcp.cs:1067) — but neither Init() nor InitBrain() creates it; it is only ever created on demand, the
/// first time a graph-path/graph-explain query runs GraphBfs. Folding it into this eagerly-applied
/// schema would give an empty store an extra sqlite_master row that today's Init()+InitBrain() never
/// produce, breaking the slice-3b parity oracle. Flagged for slice 3c or the graph-indexer slice (14),
/// where GraphBfs's call site is repointed at the provider layer and the parity oracle can account for
/// it; not resolved here per the card's "stop and report if section 3.1 conflicts with the code".
/// </summary>
public sealed class GraphSchema : ISchemaProvider
{
    public string Name => "Graph";

    public IReadOnlyList<string> Statements { get; } =
    [
        // aitm.cs:382
        "CREATE TABLE IF NOT EXISTS edges(id INTEGER PRIMARY KEY, symbol TEXT, contract TEXT, project TEXT, file TEXT, line INTEGER, usage TEXT, hardcoded INTEGER);",
        // aitm.cs:390
        "CREATE TABLE IF NOT EXISTS projects(name TEXT PRIMARY KEY, root TEXT, lang TEXT, globs TEXT);",
        // aitm.cs:391
        "CREATE TABLE IF NOT EXISTS edge_candidates(id INTEGER PRIMARY KEY, symbol TEXT, contract TEXT, project TEXT, file TEXT, line INTEGER, usage TEXT, hardcoded INTEGER, status TEXT);",
    ];
}
