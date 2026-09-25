using Aitm.Store.Schema;

namespace Aitm.Graph.Schema;

/// <summary>
/// Graph schema step 1 (RESTRUCTURE.md slice 14): the 2 indexes only <c>index-code.mjs</c> creates —
/// <c>edges_symbol_idx</c> and <c>edges_ident_idx</c> (index-code.mjs:156-157) — plus
/// <c>edges_file_idx</c>, created twice today by the old hosts (aitm.cs:2564, mcp.cs:1067). All three
/// index the same <c>edges</c> table Graph already owns (<see cref="GraphSchema"/>), so they are grouped
/// as one additive, idempotent step applied through <see cref="Store.Schema.SchemaRunner.Run"/> rather
/// than folded into the base DDL — <see cref="GraphSchema"/> stays exactly what an empty store's
/// Init()+InitBrain() produce today (the slice-3b parity oracle), and this step is what a store
/// migrates through afterwards.
///
/// <see cref="Store.Schema.SchemaRunner.Run"/> is not wired into the live init/mcp startup path yet, so
/// the old hosts keep creating <c>edges_file_idx</c> lazily on demand (GraphBfs, aitm.cs:2562; mcp.cs:1067)
/// until they retire — this step and the lazy calls both use <c>CREATE INDEX IF NOT EXISTS</c>, so
/// running both against the same store is safe.
/// </summary>
public sealed class GraphIndexSchema : ISchemaProvider
{
    public string Name => "GraphIndexes";

    public IReadOnlyList<string> Statements { get; } =
    [
        // index-code.mjs:156 — exact-match lookups and `impact <symbol>` both scan edges without this.
        "CREATE INDEX IF NOT EXISTS edges_symbol_idx ON edges(symbol)",
        // index-code.mjs:157 — the idempotency check index-code's own bulk insert relies on.
        "CREATE INDEX IF NOT EXISTS edges_ident_idx ON edges(symbol, file, line)",
        // aitm.cs:2564 / mcp.cs:1067 — GraphBfs's per-hop file lookup.
        "CREATE INDEX IF NOT EXISTS edges_file_idx ON edges(file, symbol, line)",
    ];
}
