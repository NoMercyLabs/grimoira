using Aitm.Graph.Schema;
using Aitm.Store.Schema;
using Microsoft.Data.Sqlite;

namespace Aitm.Brain.Schema;

/// <summary>
/// RESTRUCTURE.md phase 3 slice 31b ("the rest of relative paths"): once <c>edges.file_rel</c> exists
/// (<see cref="GraphFileRelSchema"/>, slice 31), <c>legacy_consumes</c> (<see cref="BrainSchema"/>) is
/// recreated to also expose the raw <c>project</c> and <c>file_rel</c> columns alongside its existing
/// <c>file</c>, so <c>brain_impact</c> (<see cref="Tools.BrainImpactTool"/>) can resolve a full path for
/// THIS machine the same way the 3 <c>graph_*</c> tools and <c>impact</c> already do (falling back to
/// <c>file</c> where <c>file_rel</c> is NULL) — rather than only ever answering with the path baked in at
/// index time.
///
/// Applied through <see cref="SchemaRunner.Run"/> (backup-first, rule 3 in section 3.2: additive and
/// idempotent). Guarded twice, like <see cref="GraphFileRelSchema"/>: nothing to do when <c>edges</c>
/// itself has no <c>file_rel</c> column yet (nothing to expose), and nothing to do once the view already
/// exposes it (checked with <see cref="GraphFileRelSchema.HasColumn"/> against the view itself — SQLite's
/// <c>PRAGMA table_info</c> works on a view the same way it works on a table), so a second run is a no-op.
/// </summary>
public sealed class BrainLegacyConsumesFileRelSchema : ISchemaProvider
{
    public string Name => "BrainLegacyConsumesFileRel";

    public IReadOnlyList<string> Statements { get; }

    public BrainLegacyConsumesFileRelSchema(SqliteConnection connection)
    {
        bool edgesHasFileRel = GraphFileRelSchema.HasColumn(connection, "edges", "file_rel");
        bool viewHasFileRel = GraphFileRelSchema.HasColumn(connection, "legacy_consumes", "file_rel");
        Statements = edgesHasFileRel && !viewHasFileRel
            ?
            [
                "DROP VIEW IF EXISTS legacy_consumes;",
                """
                CREATE VIEW legacy_consumes AS
                  SELECT COALESCE(a.k,'proj:'||e.project) AS s, 'consumes' AS p,
                         'contract:'||e.contract||'.'||e.symbol AS o, e.file, e.line, e.usage, e.hardcoded,
                         e.project AS project, e.file_rel AS file_rel
                  FROM edges e LEFT JOIN proj_alias a ON a.short = e.project;
                """,
            ]
            : Array.Empty<string>();
    }
}
