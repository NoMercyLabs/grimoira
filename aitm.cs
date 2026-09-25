#:package Microsoft.Data.Sqlite@10.0.9
#:package SQLitePCLRaw.bundle_e_sqlite3@3.0.3
// RESTRUCTURE.md slice 24, CLI lane part 1: aitm.cs's Store and Facts verbs now dispatch to their
// tool classes instead of carrying the logic inline.
// RESTRUCTURE.md slice 24, CLI lane part 2: the Memory and Docs verbs do the same.
// RESTRUCTURE.md slice 24, CLI lane part 3: aitm.cs's Graph verbs (project, projects, forget-project,
// extract-edges, candidates, promote, promote-all, seed-edges, impact, graph-query, graph-path,
// graph-explain) dispatch to their Aitm.Graph tool classes the same way.
// RESTRUCTURE.md slice 24, CLI lane part 4a: the Brain-read sub-verbs (core, scope, common, place, why,
// stale, gaps, verify, audit, export) do the same, nested inside BrainCmd's own switch.
// RESTRUCTURE.md slice 24, CLI lane part 4b: the Brain write verbs (learn, learn-batch, set-hard, merge,
// forget, unlink, shed-node, tidy, distill) do the same.
#:project src/Aitm.Store/Aitm.Store.csproj
#:project src/Aitm.Facts/Aitm.Facts.csproj
#:project src/Aitm.Memory/Aitm.Memory.csproj
#:project src/Aitm.Docs/Aitm.Docs.csproj
#:project src/Aitm.Graph/Aitm.Graph.csproj
#:project src/Aitm.Brain/Aitm.Brain.csproj
#:project src/Aitm.Server/Aitm.Server.csproj
// AITM foundation — core slice: a per-instance SQLite store with a CURRENT projection
// (one row per entity, the only thing normal reads touch) plus an append-only MUTATIONS
// log (cold; read only for trace/rollback). FTS5-ranked knowledge lookup. Generic: the
// engine is project-agnostic, all behaviour driven by the per-instance store under ~/.aitm.
// RESTRUCTURE.md slice 29b: the dispatch body now lives in Aitm.Server's CliDispatch.Run, which
// returns the exit code instead of calling Environment.Exit. This file is the shim that runs it.
using Aitm.Server.Data;

return CliDispatch.Run(args, Directory.GetCurrentDirectory(), Console.Out, Console.Error);
