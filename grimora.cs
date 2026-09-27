#:package Microsoft.Data.Sqlite@10.0.9
#:package SQLitePCLRaw.bundle_e_sqlite3@3.0.3
// RESTRUCTURE.md slice 24, CLI lane part 1: grimora.cs's Store and Facts verbs now dispatch to their
// tool classes instead of carrying the logic inline.
// RESTRUCTURE.md slice 24, CLI lane part 2: the Memory and Docs verbs do the same.
// RESTRUCTURE.md slice 24, CLI lane part 3: grimora.cs's Graph verbs (project, projects, forget-project,
// extract-edges, candidates, promote, promote-all, seed-edges, impact, graph-query, graph-path,
// graph-explain) dispatch to their Grimora.Graph tool classes the same way.
// RESTRUCTURE.md slice 24, CLI lane part 4a: the Brain-read sub-verbs (core, scope, common, place, why,
// stale, gaps, verify, audit, export) do the same, nested inside BrainCmd's own switch.
// RESTRUCTURE.md slice 24, CLI lane part 4b: the Brain write verbs (learn, learn-batch, set-hard, merge,
// forget, unlink, shed-node, tidy, distill) do the same.
#:project src/Grimora.Store/Grimora.Store.csproj
#:project src/Grimora.Facts/Grimora.Facts.csproj
#:project src/Grimora.Memory/Grimora.Memory.csproj
#:project src/Grimora.Docs/Grimora.Docs.csproj
#:project src/Grimora.Graph/Grimora.Graph.csproj
#:project src/Grimora.Brain/Grimora.Brain.csproj
#:project src/Grimora.Server/Grimora.Server.csproj
// Grimora foundation — core slice: a per-instance SQLite store with a CURRENT projection
// (one row per entity, the only thing normal reads touch) plus an append-only MUTATIONS
// log (cold; read only for trace/rollback). FTS5-ranked knowledge lookup. Generic: the
// engine is project-agnostic, all behaviour driven by the per-instance store under ~/.grimora.
// RESTRUCTURE.md slice 29b: the dispatch body now lives in Grimora.Server's CliDispatch.Run, which
// returns the exit code instead of calling Environment.Exit. This file is the shim that runs it.
using Grimora.Server.Data;

return CliDispatch.Run(args, Directory.GetCurrentDirectory(), Console.Out, Console.Error);
