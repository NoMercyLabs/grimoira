# grimora

**Agent In The Middle.**

It sits between the input field and the model, and it keeps both sides on the right path. That direction matters: this is a shared contract, not the agent's private memory.

It holds everything learned about the product being built. Every exchange is context that accumulates into where the product is going, so a decision argued four months ago still applies to a change made today. The job is to stop the same things being explained twice, and to stop the "you just broke this by fixing that" class of regression, because a decision made now still matters two years from now.

One SQLite store per instance under `~/.grimora/<instance>/grimora.db`, with separate channels for verified facts, durable rules, absorbed docs, conversation history, and a code graph.

The conversation channel is a primary asset, not noise around the facts. It carries the reasoning behind decisions, which is the part that prevents regressions; anything that ranks it as low-value defeats the purpose of the store.

## Layout

| File | What it is |
| --- | --- |
| `grimora.cs` | The CLI. File-based C# app, builds to `bin-cli/`. |
| `mcp.cs` | MCP server exposing the store as tools. Builds to `bin/`. |
| `grimora index-code` | Bulk indexer for the public declaration surface of registered projects (C#, `IndexCodeTool`). |

## Build

## Install as a plugin

Grimora is a Claude Code plugin. It bundles the MCP server (`.mcp.json`, launched through
the published CLI), the knowledge skill (`skills/grimora/SKILL.md`), two agents
(`agents/knowledge-lookup.md` for read-only lookups, `agents/knowledge-writer.md` for
deciding what to stage and flush), a maintenance slash command
(`commands/grimora-maintain.md`), and the hard-gate hooks only — see below.

The repo is its own marketplace, so it installs directly:

```
/plugin marketplace add NoMercyLabs/grimora
/plugin install grimora@nomercylabs
```

The plugin needs only `dotnet` (.NET 10 SDK). `.mcp.json` runs `dotnet ${CLAUDE_PLUGIN_DATA}/current/bin-cli/grimora.dll mcp`. Build output is gitignored and never shipped: on the first session after an install or update, the SessionStart step `bootstrap.cs` (a .NET file-based app) builds the CLI and server into the plugin data folder in the background and points `current` at the finished build. Until that build ends (a few minutes), the hooks and the MCP server have no dll: `dotnet` prints its "Could not execute" text and exits 1, so Claude Code may show a failed hook or a failed `grimora` MCP server for that first session. Reconnect it with `/mcp` after the build.

### What the plugin's hooks do

`hooks/hooks.json` carries only the hard gates — the ones that cannot be a judgment
call and must run outside the model's reasoning, not the ones that grade Claude's own
answers:

- `dotnet .../grimora.dll hook <event>` (UserPromptSubmit, PreCompact, PostToolUse `Write|Edit|MultiEdit|NotebookEdit`, SessionEnd) — runs the event through the
  published CLI, which saves and restores the compaction anchors and
  folds the finished session back into the store.

Everything that used to judge the quality of Claude's own answer by pattern-matching
text (`hedge-guard`, `goal-guard`, `proof-guard`, `continue-guard`, `population-guard`,
`loop-guard`, `blast-radius`) and everything that force-fed the store onto every
Grep/Glob/Read/prompt (`brain-gate`, `brain-read-gate`, `brain-harvest`,
`brain-history`, `prompt-recall`, `context-watch`, `subagent-context`, `brain-capture`,
`synthesis-capture`, `brain-context`, `session-continue`) never belonged in the plugin
and has been deleted from the repo entirely (`docs/RESTRUCTURE.md` section 2.4): the
skill and the two agents reach the same tools with judgment instead of a blind script
on every turn.

If you previously wired Grimora hooks by hand in `settings.json`, do not enable this
plugin until those direct entries are removed in the same sitting — running both at
once double-executes a hook that appears in both places. Run
`grimora hooks-doctor --project C:/Projects/NoMercy` to check hook overlap without
running hooks or showing command arguments; `verify.ps1 -Project C:/Projects/NoMercy`
includes this check.

## The enforcement loop (retired)

`brain-gate`, `brain-harvest` and `brain-capture` used to form a closed loop: `brain-gate` refused a filesystem search the store could already answer and handed back the hits; when the store genuinely missed, the search ran and the miss was recorded as a gap; `brain-harvest` folded whatever that search found back into the code graph and closed the gap; `brain-capture` blocked the end of the turn if gaps were opened and nothing was taught back. All three force-fed recall into ordinary tool calls and are deleted (`docs/RESTRUCTURE.md` section 2.4). Gaps stay visible through the `brain_gaps` / `gaps` tool instead of being enforced by a hook.

Every hook still in the plugin fails open: a guard error never blocks a real tool call.

## Store notes

The store runs in WAL mode. Under the default rollback journal a single writer blocks every reader, and the long doc indexer holds the write lock for minutes, which made concurrent reads throw `SQLITE_BUSY` and crash. Both the CLI and the MCP server set `journal_mode=WAL` and `busy_timeout=5000` on open.

Node's bundled SQLite has no FTS5. Anything touching an `*_fts` table has to go through the C# CLI or a `Microsoft.Data.Sqlite` process, or the store and its search index drift apart silently.
