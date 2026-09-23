# aitm

**Agent In The Middle.**

It sits between the input field and the model, and it keeps both sides on the right path. That direction matters: this is a shared contract, not the agent's private memory.

It holds everything learned about the product being built. Every exchange is context that accumulates into where the product is going, so a decision argued four months ago still applies to a change made today. The job is to stop the same things being explained twice, and to stop the "you just broke this by fixing that" class of regression, because a decision made now still matters two years from now.

One SQLite store per instance under `~/.aitm/<instance>/aitm.db`, with separate channels for verified facts, durable rules, absorbed docs, conversation history, and a code graph.

The conversation channel is a primary asset, not noise around the facts. It carries the reasoning behind decisions, which is the part that prevents regressions; anything that ranks it as low-value defeats the purpose of the store.

## Layout

| File | What it is |
| --- | --- |
| `aitm.cs` | The CLI. File-based C# app, builds to `bin-cli/`. |
| `mcp.cs` | MCP server exposing the store as tools. Builds to `bin/`. |
| `brain-lib.mjs` | Shared lookup: tokenise, scan every channel, score, format. |
| `brain-gate.mjs` | PreToolUse `Grep\|Glob`. Answers from the store and denies the blind search. |
| `brain-harvest.mjs` | PostToolUse `Grep\|Glob`. Writes search results back as code edges. |
| `brain-capture.mjs` | Stop. Blocks once when a session leaves gaps unfilled. |
| `index-code.mjs` | Bulk indexer for the public declaration surface of registered projects. |
| `prompt-recall.mjs` | UserPromptSubmit auto-recall. |
| `session-*.mjs` | Session lifecycle indexers. |
| `*-guard.mjs` | Stop-hook behaviour guards. |

## Build

### NoMercy process ownership pilot

New launches through `launch-mcp.mjs` record ownership when their project directory
is inside the sibling `NoMercy` checkout. Other workspaces retain their existing
startup behavior. Existing sessions are not adopted or restarted.

Inspect records with `node process-owner.mjs status`. Records live in the workspace's
ignored `.scratch/process-owners/` directory. They contain launcher and child IDs,
timestamps, and lifecycle state; no command arguments or environment values.

Only the live launcher can cancel its original child. The status command never kills
processes. A record left as running after a crash has unverified liveness and must not
be used to kill a PID. Forced-crash cleanup, application session identity, descendant
ownership are not implemented by this initial pilot. The latest 50 completed records
are retained during ordinary operation. Housekeeping examines at most 1,000 entries;
unresolved records are preserved for inspection rather than automatically deleted.

Validate with `node --test process-owner.test.mjs`. No global hook changes are needed.

In NoMercy, Claude and Codex project registrations launch this server. Its
`workspace_capabilities` tool calls the workspace's bounded capability lookup and
returns reviewed prerequisites and limitations when available. It only discovers
tools; it does not run them. The lookup requires Python and has a 25-second timeout.
Restart an existing agent session to load a new tool registration.

```powershell
./build-cli.ps1   # -> bin-cli/aitm.exe
./build-mcp.ps1   # -> bin/mcp.dll
```

## Install as a plugin

The repo is its own marketplace, so it installs directly:

```
/plugin marketplace add NoMercyLabs/aitm
/plugin install aitm@nomercylabs
```

The hooks are plain node and work as soon as the plugin is installed. The CLI and MCP server need `build-cli.ps1` / `build-mcp.ps1` first, since build output is not committed.

If you previously wired these hooks by hand in `settings.json`, migrate those entries before enabling the plugin. On the current NoMercy machine, all 14 plugin hooks also appear as direct global hooks. Another 14 direct AITM hooks are absent from this plugin. Enabling it now duplicates work; removing all direct hooks loses behavior. The NoMercy MCP registration works independently while this migration remains open.

Run `node hook-doctor.mjs --project C:/Projects/NoMercy` to check hook overlap without running hooks or showing command arguments. It fails when settings enable overlapping plugin hooks or repeat a direct AITM hook. `verify.ps1 -Project C:/Projects/NoMercy` includes this check.

## The enforcement loop

The hooks form a closed loop. `brain-gate` refuses a filesystem search the store can already answer and hands back the hits. When the store genuinely misses, the search runs and the miss is recorded as a gap. `brain-harvest` folds whatever that search found back into the code graph and closes the gap. `brain-capture` blocks the end of the turn if gaps were opened and nothing was taught back. Coverage therefore grows from ordinary work rather than from anyone remembering to record things.

The gate fires at most once per search signature per session, so re-issuing an identical search always reaches the filesystem. Every hook fails open: a guard error never blocks a real tool call.

## Store notes

The store runs in WAL mode. Under the default rollback journal a single writer blocks every reader, and the long doc indexer holds the write lock for minutes, which made concurrent reads throw `SQLITE_BUSY` and crash. Both the CLI and the MCP server set `journal_mode=WAL` and `busy_timeout=5000` on open.

Node's bundled SQLite has no FTS5. Anything touching an `*_fts` table has to go through the C# CLI or a `Microsoft.Data.Sqlite` process, or the store and its search index drift apart silently.
