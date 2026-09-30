# Grimora

Grimora is a long-term memory for Claude Code.

Claude forgets everything when a session ends. Grimora keeps what matters: facts you verified, rules you set, decisions you argued over, and how your projects connect. The next session, in any of your projects, can ask for that knowledge instead of guessing. It runs on your machine, and nothing leaves it.

## What it does for you

Claude reaches Grimora through 26 tools and through the `grimora` command. Day to day, it can:

- Look up a verified fact, such as a base URL, a file path, a config key or a port. If Grimora does not know, it says so and logs the gap. It does not guess.
- Recall a standing rule or a past decision about how to work in a project.
- Search what was said in earlier conversations.
- Search your plans, specs and docs that Grimora has absorbed.
- Answer "what breaks if I change this?" across projects, with the files and lines that use a symbol.
- Record what a session learned, so the next one starts ahead, and keep todos and findings.

Its answers are leads with a source. The source file and the running code are still the proof; a memory is context, not a verdict.

## How it works

Grimora is a Claude Code plugin. It ships two skills (`grimora` for everyday use, `grimora-init` for the first start), two agents, a maintenance command, an MCP server, and a small set of hooks. The MCP server and the hooks call a thin `grimora` command. That command talks to a background service over a local named pipe (a Unix socket on macOS and Linux), so no network port is open. The service starts on the first call and exits after 30 quiet minutes. Hooks save your place before Claude compacts a conversation and fold the finished session into the store. Everything lives in SQLite files on your disk, one file per project instance.

## Install

Requirements:

- Claude Code.
- The .NET 10 SDK (`dotnet`). The plugin builds its own command and service with it. Nothing else is required — no Node, no separate runtime.

The repository is private for now. You need access to `NoMercyLabs/grimora` on GitHub. Then, in Claude Code:

```
/plugin marketplace add NoMercyLabs/grimora
/plugin install grimora@nomercylabs
```

## First start

There is no login and no setup screen. When you want to choose what Grimora indexes, ask Claude to "set up grimora": the `grimora-init` skill asks three questions (where the store lives, which projects to index, what to import), runs the setup, and proves the first answer. Without it, the first call creates an empty store for the project and the hooks fill it session by session.

The first session after an install or an update has no built command yet. A one-time step starts a background build and Claude Code may show a failed hook or a failed `grimora` connection in `/mcp` for that first session, because the build has not finished. The build takes a few minutes. Reconnect with `/mcp` once it is done; your session works normally in the meantime, just without Grimora.

The first call after the build creates your data folder:

```
~/.grimora/<instance>/grimora.db
```

An instance is one store. Its name comes from `GRIMORA_INSTANCE`, or from the folder of the project Claude is working in.

If you used Grimora under its earlier name, AITM, your data is in `~/.aitm`. The first time the service starts and `~/.grimora` does not exist yet, it copies `~/.aitm` there once, using SQLite's own backup method so a running old copy is never corrupted mid-copy. It never changes or deletes `~/.aitm`.

## Everyday use

In Claude Code you mostly do nothing. Claude calls the tools when the skill tells it to. Two agents back that up: one only reads, the other decides what is worth recording after a task finishes.

From a terminal you can use the command directly:

```
grimora help
```

lists every verb. Some verbs you will use directly:

```
grimora add --term "api base url" --value "https://api.example.test" --category url --source "docs/api.md" --instance demo
grimora query "api base url" --instance demo
grimora todo "write the release notes" --instance demo
grimora projects --instance demo
grimora stats --instance demo
```

To search an entire transcript population, use `chat` rather than the short ranked `recall` answer:

```
grimora index-chat --from ~/.claude/projects/<project> --instance demo
grimora chat list --session <session-id> --kind human --page-size 500 --instance demo
grimora chat first --session <session-id> --instance demo
grimora chat list --kind assistant --from 2026-09-26 --to 2026-09-29 --page 2 --instance demo
grimora chat list --kind tool_result_doc --path "friends message.md" --full --instance demo
grimora chat count --match '"KMP" AND "deploy"' --by month --instance demo
grimora chat commands --command /goal --full --page-size 500 --instance demo
grimora chat parity --old ~/.aitm/<instance>/aitm.db --instance demo
```

`chat list` defaults to short previews and reports the total, shown, and remaining rows. Increase `--page` until the remaining count is zero; `--full` returns whole text. `--kind human` includes slash commands and excludes compaction, hook, skill, sidechain, and system messages. MCP callers can use `chat_list` for paged session, kind, time, and path filters, or `chat_count` for FTS match counts. `chat import-missing --old <db>` imports any rows found by the parity check, after backing up the current store.

A miss looks like this, on purpose. Grimora refuses rather than guesses:

```
no confident answer for "demo" - not in the knowledge base (refusing rather than guessing; gap logged).
```

Service control, if you ever need it:

```
grimora service status
grimora service stop
```

You rarely need `grimora service start` by hand; the first call starts it for you.

`/grimora-maintain` is a slash command for occasional admin work: rebuilding the code graph, exporting or importing a store, forgetting a project. It is never run automatically, and it asks before it deletes anything.

## Privacy

Grimora stores facts, rules, absorbed docs, conversation history, todos, findings, and a code graph of declarations and usage sites, never your source code itself. All of it stays in your data folder. The conversation index includes user messages, assistant replies, pasted content, and results of reading untracked files; these can contain private text from a transcript, even when the original file has been deleted.

Before conversation text is stored, Grimora replaces token-shaped strings with a redaction marker. It covers JWTs, bearer tokens, common key prefixes such as GitHub, OpenAI, AWS and Slack, and PEM private keys. If a check cannot finish in time, the whole text is redacted rather than left unscrubbed.

Grimora holds no secrets of its own and opens no network port. On macOS and Linux its data folder is set to user-only permissions.

## Configuration

Every setting is an environment variable, and every one is optional:

- `GRIMORA_DATA_DIR` — the data folder. Default `~/.grimora`.
- `GRIMORA_INSTANCE` — which store to use.
- `GRIMORA_IDLE_MINUTES` — quiet minutes before the background service exits. Default 30.
- `GRIMORA_SKIP_PROJECTS` — project names, comma separated, that the code indexer skips.

The old `AITM_*` names are still read for one release if their `GRIMORA_*` equivalent is unset.

## Upgrade and uninstall

Update through Claude Code's `/plugin` menu. The next session builds the new version in the background the same way the first install did. Your data folder is never touched by an update.

To uninstall, remove the plugin from the `/plugin` menu. Your data stays in `~/.grimora` until you delete that folder yourself.

## Troubleshooting

**Grimora seems missing right after an install or update.** It is still building in the background. Wait a few minutes and start a new session, or reconnect with `/mcp`.

**The service is not running.** Run `grimora service status`, then `grimora service start` if needed.

**The service exited by itself.** That is the idle exit after 30 quiet minutes. The next call starts it again.

## Building from source

You need the .NET 10 SDK.

```
./build-cli.sh      # or build-cli.ps1
./build-server.sh   # or build-server.ps1
./build.sh           # or build.ps1: builds everything and runs the tests
```

Every build script has a matching bash and PowerShell twin with the same flags. Run the tests directly with:

```
dotnet test Grimora.sln
```

`./verify.sh` (or `verify.ps1`) runs the checks that must pass before a commit.

## Contributing

The design notes are in `docs/`. `docs/RESTRUCTURE.md` explains how the code is laid out and why. Read it before you change a project boundary.

## Licence

Apache License 2.0. See `LICENSE`.
