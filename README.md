# Grimoira

Grimoira is a long-term memory for Claude Code.

Claude forgets everything when a session ends. Grimoira keeps what matters: facts you verified, rules you set, decisions you argued over, and how your projects connect. The next session, in any of your projects, can ask for that knowledge instead of guessing. It runs on your machine, and nothing leaves it.

Grimoira is built to be Claude Code's memory and knowledge layer. Once installed, it takes part in every session, so what was learned is not lost and repeated work goes through the same tools. [HOW-IT-WORKS.md](HOW-IT-WORKS.md) says exactly what it changes, what it stores, what it costs and how to turn it off.

## What it does for you

Claude reaches Grimoira through 26 tools and through the `grimoira` command. Day to day, it can:

- Look up a verified fact, such as a base URL, a file path, a config key or a port. If Grimoira does not know, it says so and logs the gap. It does not guess.
- Recall a standing rule or a past decision about how to work in a project.
- Search what was said in earlier conversations.
- Search your plans, specs and docs that Grimoira has absorbed.
- Answer "what breaks if I change this?" across projects, with the files and lines that use a symbol.
- Record what a session learned, so the next one starts ahead, and keep todos and findings.

Its answers are leads with a source. The source file and the running code are still the proof; a memory is context, not a verdict.

## How it works

Grimoira is a Claude Code plugin. It ships two skills (`grimoira` for everyday use, `grimoira-init` for the first start), two agents, a maintenance command, an MCP server, and a small set of hooks. The MCP server and the hooks call a thin `grimoira` command. That command talks to a background service over a local named pipe (a Unix socket on macOS and Linux), so no network port is open. The service starts on the first call and exits after 30 quiet minutes. Hooks save your place before Claude compacts a conversation and fold the finished session into the store. Everything lives in SQLite files on your disk, one file per project instance.

## Install

Requirements:

- Claude Code.
- The .NET 10 SDK (`dotnet`). The plugin builds its own command and service with it. Nothing else is required — no Node, no separate runtime.

In Claude Code:

```
/plugin marketplace add NoMercyLabs/skills
/plugin install grimoira@nomercylabs
```

`NoMercyLabs/skills` is the one install list for every NoMercy Labs plugin. If you already added `NoMercyLabs/grimoira`, keep it: it lists the same plugins.

## First start

There is no login and no setup screen. When you want to choose what Grimoira indexes, ask Claude to "set up grimoira": the `grimoira-init` skill asks three questions (where the store lives, which projects to index, what to import), runs the setup, and proves the first answer. Without it, the first call creates an empty store for the project and the hooks fill it session by session.

The first session after an install or an update has no built command yet. A one-time step starts a background build and Claude Code may show a failed hook or a failed `grimoira` connection in `/mcp` for that first session, because the build has not finished. The build takes a few minutes. Reconnect with `/mcp` once it is done; your session works normally in the meantime, just without Grimoira.

The first call after the build creates your data folder:

```
~/.grimoira/<instance>/grimoira.db
```

An instance is one store. Its name comes from `GRIMOIRA_INSTANCE`, or from the folder of the project Claude is working in.

## If you used it as Grimora

Grimoira was called Grimora until version 2.0.0 (and AITM before that). Nothing you have is lost.

- Your data: the first time the service starts and `~/.grimoira` does not exist yet, it copies `~/.grimora` there once (or `~/.aitm`, if `~/.grimora` does not exist), using SQLite's own backup method so a running old copy is never corrupted mid-copy. The databases are renamed to `grimoira.db` inside the copy.
- Disk space: the copy needs free disk space equal to the size of your store. Check it before the first start if your store is large.
- The backup: `~/.grimora` (or `~/.aitm`) is never changed or deleted. It stays as your backup. You may delete it once Grimoira runs and you have checked your data.
- Settings: `GRIMORA_*` and `AITM_*` environment variables are still read and become `GRIMOIRA_*`. A `GRIMOIRA_*` variable that is set wins.
- The plugin: a rename makes a new plugin, not an update. Run `/plugin install grimoira@nomercylabs`, then `/plugin uninstall grimora@nomercylabs`. The old name stays in the marketplace as a stub that only prints this advice.
- The repository: it moved to `NoMercyLabs/grimoira`. GitHub redirects the old address.

## Everyday use

In Claude Code you mostly do nothing. Claude calls the tools when the skill tells it to. Two agents back that up: one only reads, the other decides what is worth recording after a task finishes.

From a terminal you can use the command directly:

```
grimoira help
```

lists every verb. Some verbs you will use directly:

```
grimoira add --term "api base url" --value "https://api.example.test" --category url --source "docs/api.md" --instance demo
grimoira query "api base url" --instance demo
grimoira todo "write the release notes" --instance demo
grimoira projects --instance demo
grimoira stats --instance demo
```

To search an entire transcript population, use `chat` rather than the short ranked `recall` answer:

```
grimoira index-chat --from ~/.claude/projects/<project> --instance demo
grimoira chat list --session <session-id> --kind human --page-size 500 --instance demo
grimoira chat first --session <session-id> --instance demo
grimoira chat list --kind assistant --from 2026-09-26 --to 2026-09-29 --page 2 --instance demo
grimoira chat list --kind tool_result_doc --path "friends message.md" --full --instance demo
grimoira chat count --match '"KMP" AND "deploy"' --by month --instance demo
grimoira chat commands --command /goal --full --page-size 500 --instance demo
grimoira chat parity --old ~/.aitm/<instance>/aitm.db --instance demo
```

`chat list` defaults to short previews and reports the total, shown, and remaining rows. Increase `--page` until the remaining count is zero; `--full` returns whole text. `--kind human` includes slash commands and excludes compaction, hook, skill, sidechain, and system messages. MCP callers can use `chat_list` for paged session, kind, time, and path filters, or `chat_count` for FTS match counts. `chat import-missing --old <db>` imports any rows found by the parity check, after backing up the current store.

A miss looks like this, on purpose. Grimoira refuses rather than guesses:

```
no confident answer for "demo" - not in the knowledge base (refusing rather than guessing; gap logged).
```

Service control, if you ever need it:

```
grimoira service status
grimoira service stop
```

You rarely need `grimoira service start` by hand; the first call starts it for you.

`/grimoira-maintain` is a slash command for occasional admin work: rebuilding the code graph, exporting or importing a store, forgetting a project. It is never run automatically, and it asks before it deletes anything.

## Privacy

Grimoira stores facts, rules, absorbed docs, conversation history, todos, findings, and a code graph of declarations and usage sites, never your source code itself. All of it stays in your data folder. The conversation index includes user messages, assistant replies, pasted content, and results of reading untracked files; these can contain private text from a transcript, even when the original file has been deleted.

Before conversation text is stored, Grimoira replaces token-shaped strings with a redaction marker. It covers JWTs, bearer tokens, common key prefixes such as GitHub, OpenAI, AWS and Slack, and PEM private keys. If a check cannot finish in time, the whole text is redacted rather than left unscrubbed.

Grimoira holds no secrets of its own and opens no network port. On macOS and Linux its data folder is set to user-only permissions.

## Configuration

Every setting is an environment variable, and every one is optional:

- `GRIMOIRA_DATA_DIR` — the data folder. Default `~/.grimoira`.
- `GRIMOIRA_INSTANCE` — which store to use.
- `GRIMOIRA_IDLE_MINUTES` — quiet minutes before the background service exits. Default 30.
- `GRIMOIRA_SKIP_PROJECTS` — project names, comma separated, that the code indexer skips.

The old `AITM_*` names are still read for one release if their `GRIMOIRA_*` equivalent is unset.

## Upgrade and uninstall

Update through Claude Code's `/plugin` menu. The next session builds the new version in the background the same way the first install did. Your data folder is never touched by an update.

To uninstall, remove the plugin from the `/plugin` menu. Your data stays in `~/.grimoira` until you delete that folder yourself.

## Troubleshooting

**Grimoira seems missing right after an install or update.** It is still building in the background. Wait a few minutes and start a new session, or reconnect with `/mcp`.

**The service is not running.** Run `grimoira service status`, then `grimoira service start` if needed.

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
dotnet test Grimoira.sln
```

`./verify.sh` (or `verify.ps1`) runs the checks that must pass before a commit.

## Contributing

`docs/ARCHITECTURE.md` explains how the code is laid out and why. Read it before you change a project boundary. Design history lives in a private ledger; code comments that cite `RESTRUCTURE.md` slices refer to it.

## Licence

Apache License 2.0. See `LICENSE`.
