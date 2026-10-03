# Architecture

This page describes how Grimoira is built. For install, configuration, privacy and troubleshooting, see the [README](../README.md).

## Overview

Grimoira is a Claude Code plugin. It ships a skill, two agents (`knowledge-lookup`, `knowledge-writer`), one maintenance command (`/grimoira-maintain`), an MCP server entry and six hooks. The hooks and the MCP entry both run one thin command: `dotnet <data>/current/bin-cli/grimoira.dll`. That command keeps almost nothing in itself. It sends most verbs to a background service over a local named pipe (a Unix socket on macOS and Linux), limited to the current user, so no network port is open. The service starts on the first call and exits after 30 quiet minutes. The service owns the store: one SQLite file per instance. A few hook handlers stay in the command so they still answer when the service is down.

## The pieces

The `src/` folder holds nine projects.

- `Grimoira.Cli`: the `grimoira` executable. It takes no reference on any other Grimoira project. It runs the `hook` verbs that must work without the service, and forwards everything else to the service.
- `Grimoira.Server`: the background service. It listens on the named pipe, serves `/mcp`, `/cli` and `/hooks/{event}`, and runs the indexing jobs queued by `SessionEnd`.
- `Grimoira.Store`: the SQLite store itself: connection, data folder, the gap log, the mutation log, `history`, `stats`, `import` and `backup`.
- `Grimoira.Facts`: verified facts, todos and findings.
- `Grimoira.Memory`: rules (the memory channel) and the chat transcript index.
- `Grimoira.Docs`: absorbed docs, chunked by section, and syntheses.
- `Grimoira.Graph`: registered projects, the code graph of declarations and usage sites (`edges`), edge candidates, and the `impact`, `graph-query`, `graph-path` and `graph-explain` lookups.
- `Grimoira.Brain`: the cross-project knowledge graph (nodes, triples, slots), staged learning and the `brain_*` tools.
- `Grimoira.Hooks`: the hook handlers: the compaction brief and restore, index-on-edit, session indexing, the stop flush and pattern watching.

## Hooks

`hooks/hooks.json` registers six events. Every one runs `dotnet`. Every one fails open: an error prints nothing and never blocks the session.

- `SessionStart`, timeout 15 s. Runs `bootstrap.cs`. If the current build is present, it runs `grimoira hook SessionStart`, which starts the service when `/health` does not answer. If the build is missing or stale, it starts a background build and exits.
- `UserPromptSubmit`, timeout 10 s. Runs in the command itself. Hands back the anchors saved before the last compaction, once, on the first prompt after it.
- `PostToolUse`, matcher `Write|Edit|MultiEdit|NotebookEdit|Read|Grep|Glob|Bash`, timeout 65 s. Forwarded to the service, which picks the handler by tool name. An edit reindexes the memory or docs channel that the edited file belongs to. A Read, Grep, Glob or Bash call is counted in the `patterns` table.
- `PreCompact`, timeout 20 s. Runs in the command itself. Writes the user's own words, dirty repos and branches, changed files and open items to disk before the compaction.
- `SessionEnd`, timeout 390 s, async. Forwarded to the service, which queues the work and answers at once. In the background it folds the transcript into the chat index, absorbs the project's `.claude/` docs, and indexes every registered project's public declarations into `edges`.
- `Stop`, timeout 10 s. Forwarded to the service. If staged learning is waiting in the ledger, it flushes it into the store and says so.

The `PostToolUse` counter records a signature per call: the command shape for `Bash` and `PowerShell`, the relative path for `Read`, the pattern and folder for `Grep` and `Glob`. The counts are read back by the `patterns` MCP tool in `Grimoira.Hooks`, grouped by kind, so a repeated search can become a fact or a tool.

## Tools

The MCP entry exposes 26 tools. The text below is the help text each tool carries.

Facts, todos and findings:

- `fact`: look up a verified fact. Up to 3 hits, reinforces the usage signal.
- `log_finding`: log an unrelated finding for later.
- `open_findings`: list open findings. Latest 30, clipped.

Rules and memory:

- `rule`: recall a migrated rule or preference. Up to 4 hits.
- `shed_memory`: delete one memory by key.

Chat:

- `recall`: search past conversations. Up to 4 hits.
- `chat_list`: list transcript turns by session, kind and time; each page states total and remaining.
- `chat_count`: count transcript matches, optionally grouped by day, month, or session.

Docs:

- `doc`: search absorbed docs. Up to 3 hits, gap-logged when empty.

Graph:

- `impact`: list cross-project consumers of a changing symbol, field or contract.
- `graph_query`: find the most relevant symbols, files and docs for a question. Logs a gap on a miss.
- `graph_path`: shortest path between two symbols or files, max depth 6.
- `graph_explain`: what a symbol is, who uses it, and the docs and rules that mention it. Logs a gap on a miss.

Brain:

- `brain_core`: the hard nodes that apply every turn.
- `brain_scope`: everything in scope for working across the named projects.
- `brain_common`: what the named projects have in common. Needs 2 or more projects.
- `brain_place`: where new code of a kind belongs and how it is written here.
- `brain_recall`: natural-language question to synonym expansion, full-text seed, who uses it, governing rule and ground-truth fact.
- `brain_impact`: every cross-project consumer of a shared symbol or contract from the live edges graph.
- `brain_gaps`: what the brain could not answer. Every refused query is logged here.
- `brain_learn`: record a node, triple or slot.
- `brain_stage`: append a durable learning to the ledger without writing it.
- `brain_flush`: commit every staged learning into the brain in one transaction, then clear the ledger.

Store:

- `history`: the change history (the append-only mutation log) for an entity. Latest 30, newest first.

Workspace:

- `workspace_capabilities`: find existing tools by purpose; returns a few source paths, never executes what it finds.
- `workspace_search`: search fixed text or filenames in one registered repository.

### CLI verbs

`grimoira help` lists the verbs. Grouped the same way, with the help text:

Store and instance:

- `init [--full --root <dir>]`: create or open the instance; `--full` also registers projects and indexes code, docs, memory, chat.
- `import --from <db>`: merge another grimoira.db into this one.
- `history <term>`: show the cold mutation log for an entity.
- `stats`: channel counts for the instance.
- `eval`: run the retrieval eval set.
- `hooks-doctor [--project <dir>]`: find duplicate hook registrations (plugin vs. direct).

Facts, todos and findings:

- `add [--provenance stated|inferred]`: add a fact (provenance: who established it).
- `query <terms>`: look up a verified fact.
- `shed-fact --key <term>`: forget one fact by key.
- `index-packages [--root <dir>]`: index package.json identities.
- `todo | todos | done <id>`: manage todos.
- `finding | findings | resolve <id>`: manage findings.

Rules and memory:

- `index-memory --from <dir>`: migrate MEMORY.md files into the memory channel.
- `mem <terms> | mem --hard`: recall rules; `--hard` lists the always-on core.
- `shed-memory --key <slug>`: forget one memory by key.

Chat:

- `recall <terms>`: search past chat history.
- `chat <list|first|count|commands>`: enumerate transcript history with totals.
- `index-chat --from <path>`: ingest session transcripts into chat.

Docs:

- `index-docs --from <dir>`: absorb AI-meta docs, chunked by section.
- `doc <terms>`: search absorbed docs.
- `shed-doc --path <s>`: drop absorbed doc sections by path.
- `add-synthesis --path <dir> --from <f>`: file an answer distilled from a source set.

Graph:

- `project --name <n> --root <dir>`: register a project root.
- `projects`: list registered projects.
- `forget-project --name <n>`: unregister a project and drop its edges.
- `index-code [--project <name>]`: bulk-index registered projects' public declarations into edges.
- `extract-edges --symbol <s>`: grep edge candidates for a symbol.
- `candidates [--symbol <s>]`: list edge candidates.
- `promote <id>`: promote one candidate into the graph.
- `promote-all --symbol <s>`: authoritative replace of a symbol's edges.
- `impact <symbol>`: show consumers and contract sites of a symbol.
- `graph-query <question>`: relevant symbols, files and docs for a question, 1-hop neighbours.
- `graph-path <A> <B>`: shortest code-graph path between two symbols or files (depth up to 6).
- `graph-explain <symbol>`: what a symbol is, who uses it, docs and rules that mention it.

Brain:

- `seed-edges`: sync the curated cross-project edge seed.
- `spine-export [--to <f>]`: dump the curated spine to JSON.
- `spine-import --from <f>`: load a curated spine from JSON.
- `shed-node --key <k>`: retire a node and its links.

Service control (`grimoira service status|stop|start`) is described in the README under [Everyday use](../README.md#everyday-use).

## Data location

Each instance is one SQLite file:

```
~/.grimoira/<instance>/grimoira.db
```

The tables, by the project that creates them:

- Store: `meta`, `gaps`, `mutations`, `usage`.
- Facts: `facts`, `todos`, `findings`.
- Memory: `memory`, `chat`, `chat_history_meta`.
- Docs: `docs`.
- Graph: `projects`, `edges`, `edge_candidates`.
- Brain: `node`, `triple`, `slot`, `ref`, `pred_vocab`, `proj_alias`, `term_alias`, `distill_log`.
- Hooks: `patterns`.

Staged learning waits in a `pending-learn.jsonl` ledger next to the store until a flush commits it.

`GRIMOIRA_DATA_DIR` moves the data folder and `GRIMOIRA_INSTANCE` picks the store. Both are described in the README under [Configuration](../README.md#configuration).

## Upgrade and rollback

Build output is not committed, and every plugin version installs into a new cache folder. So right after an install or update there is no published command yet. `bootstrap.cs`, a .NET file-based app that uses the base class library only, handles this at `SessionStart`:

- The published command and service live in the plugin data folder, which is kept across plugin updates. Each build lands in `<data>/builds/<first 12 hex of the stamp>/{bin-cli,bin-server}`.
- `<data>/current` (a directory junction on Windows, a symlink elsewhere) points at the newest complete build. `hooks.json` and `.mcp.json` run `dotnet <data>/current/bin-cli/grimoira.dll` directly.
- When `current` is present and up to date, `bootstrap.cs` runs `grimoira hook SessionStart` and passes stdin, stdout and the exit code straight through.
- When it is missing or stale, `bootstrap.cs` starts `bootstrap.cs --build <data>` detached, prints one line, and exits 0. A lock file makes sure two sessions never build at once. The session goes on without Grimoira until the build is done.
- The detached build never writes into a folder that may be in use. It moves `current` only after a complete build and stamp. It keeps the previous build as the rollback and deletes older ones only when a trial rename proves nothing holds them.
- `bootstrap.cs --point <link> <target>` does the `current` swap on its own, for a repair by hand and for the tests. When it fails, the previous link stays.

There is no rollback command. The previous build stays under `<data>/builds/`, and `--point` can move `current` back to it by hand.

With no plugin data folder (a source checkout), the checkout's own `bin-cli/` and `bin-server/` are used.

## Design history

Code comments cite `RESTRUCTURE.md` slices and `PLAN.md`. Those are the internal design ledger, kept in a private repository (NoMercyLabs/grimora-internal). They record why a boundary is where it is; the code is the source of truth.
