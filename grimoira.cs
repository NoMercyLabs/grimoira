#:package Microsoft.Data.Sqlite@10.0.9
#:package SQLitePCLRaw.bundle_e_sqlite3@3.0.3
// RESTRUCTURE.md slice 24, CLI lane part 1: grimoira.cs's Store and Facts verbs now dispatch to their
// tool classes instead of carrying the logic inline.
// RESTRUCTURE.md slice 24, CLI lane part 2: the Memory and Docs verbs do the same.
// RESTRUCTURE.md slice 24, CLI lane part 3: grimoira.cs's Graph verbs (project, projects, forget-project,
// extract-edges, candidates, promote, promote-all, seed-edges, impact, graph-query, graph-path,
// graph-explain) dispatch to their Grimoira.Graph tool classes the same way.
// RESTRUCTURE.md slice 24, CLI lane part 4a: the Brain-read sub-verbs (core, scope, common, place, why,
// stale, gaps, verify, audit, export) do the same, nested inside BrainCmd's own switch.
// RESTRUCTURE.md slice 24, CLI lane part 4b: the Brain write verbs (learn, learn-batch, set-hard, merge,
// forget, unlink, shed-node, tidy, distill) do the same.
#:project src/Grimoira.Store/Grimoira.Store.csproj
#:project src/Grimoira.Facts/Grimoira.Facts.csproj
#:project src/Grimoira.Memory/Grimoira.Memory.csproj
#:project src/Grimoira.Docs/Grimoira.Docs.csproj
#:project src/Grimoira.Graph/Grimoira.Graph.csproj
#:project src/Grimoira.Brain/Grimoira.Brain.csproj
#:project src/Grimoira.Server/Grimoira.Server.csproj
// Grimoira foundation — core slice: a per-instance SQLite store with a CURRENT projection
// (one row per entity, the only thing normal reads touch) plus an append-only MUTATIONS
// log (cold; read only for trace/rollback). FTS5-ranked knowledge lookup. Generic: the
// engine is project-agnostic, all behaviour driven by the per-instance store under ~/.grimoira.
// RESTRUCTURE.md slice 29b: the dispatch body now lives in Grimoira.Server's CliDispatch.Run, which
// returns the exit code instead of calling Environment.Exit. This file is the shim that runs it.
using Grimoira.Server.Data;

if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GRIMOIRA_DATA_DIR")))
    Grimoira.Store.Data.LegacyStore.MoveIfNeeded(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Console.Error);
return CliDispatch.Run(args, Directory.GetCurrentDirectory(), Console.Out, Console.Error);
