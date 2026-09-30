---
name: grimora-init
description: Set up Grimora after a fresh install - ask where the store lives, which projects to index and what to import, then run `grimora init` and prove a first recall. Use when the user says "set up grimora", "init grimora", "grimora init", or right after `/plugin install grimora`.
---

# Grimora first start

Grimora needs three choices on a fresh install. Ask them, run `init` once, and prove the store answers. Never guess a choice; never delete or move anything.

## 1. Find the command

The plugin builds its own command in the background on the first session after an install or update. Find it before you ask anything:

- Config folder: `$CLAUDE_CONFIG_DIR` when set, else `~/.claude`.
- Command: `<config>/plugins/data/grimora-<marketplace>/current/bin-cli/grimora.dll`. Use a glob on `grimora-*` when you do not know the marketplace name.
- In a source checkout of Grimora with no plugin data folder: `bin-cli/grimora.dll` under the checkout, built by `./build-cli.sh` (or `build-cli.ps1`).

Run `dotnet <path to grimora.dll> help`. If it lists the verbs, use `dotnet <path to grimora.dll>` as `$GRIMORA` below.

If the data folder or `current` does not exist yet, the build has not finished. The SessionStart hook printed: "Grimora is building its CLI and server in the background (first session after an install or update); it is ready in a few minutes." Tell the user that, ask them to start a new session in a few minutes, and stop. Do not build by hand and do not wait in a loop.

## 2. Ask the three choices

Use AskUserQuestion. One question per call, in this order.

1. **Where the store lives.** Options: "The default, `~/.grimora`" (recommended) or "Another folder". A custom folder must be set as `GRIMORA_DATA_DIR` for every future session, or the hooks and the MCP server will use the default and see a different store. Add it to `~/.claude/settings.json` under `"env"` and export it for every command below. Never move an existing store.
2. **Which projects to index.** Options: "This project only" (recommended), "This project and other roots" (then ask for each root folder path), or "None, only import". A project is a folder with a manifest: `package.json`, a `.csproj` or `.sln`, `composer.json`, `build.gradle(.kts)`, `settings.gradle.kts`, `go.mod`, `Cargo.toml`, `pyproject.toml`, `CMakeLists.txt`, `configure` or `meson.build`. `init` scans the root and three levels below it for those. Its public declarations go into the code graph; its `.claude/` and `docs/` folders are absorbed as docs.
3. **What to import.** Multi-select: "Nothing", "An existing Grimora database, or one from the tool Grimora replaced" (then ask the path to the `.db` file), "Past Claude Code conversations" (`init --full` reads the transcripts under `~/.claude/projects`; another folder needs `index-chat --from`).

Say back the three answers in one line before you run anything.

## 3. Run init

The instance name is the project folder name, slugged (lower case, letters, digits, `-` and `_`). Run every command from the project folder so the instance resolves the same way the hooks resolve it. Pass `--instance <name>` on every command so a second project on the same machine never receives the data.

From the project root:

```
$GRIMORA init --full --root <project root> --instance <instance> [--skip-chat]
```

`--skip-chat` when the user did not choose conversations. `init --full` prints a numbered log: `[1] CLI`, `[2] projects`, `[3] code surface`, `[4] package identities`, `[5] docs`, `[6] rules`, `[7] past conversations`, `[8] ready`. A line that starts with `FAILED` stops the run: show that line, do not retry, and ask the user how to continue.

When `[2] projects` says `0 newly registered, 0 already known` and `[3] code surface` says `no registered projects for this instance`, the root has no manifest from the list above. Register it by name, the same way as an extra root below, then run `index-code` for it. Tell the user why.

Extra roots, one at a time:

```
$GRIMORA project --name <folder name> --root <root> --instance <instance>
$GRIMORA index-code --project <folder name> --instance <instance>
```

An existing database:

```
$GRIMORA import --from <path to .db> --instance <instance>
```

Conversations from a path other than the default:

```
$GRIMORA index-chat --from <path> --instance <instance>
```

Wait for each command to finish before the next. Show the user each command's last lines.

## 4. Prove it

```
$GRIMORA projects --instance <instance>
$GRIMORA stats --instance <instance>
```

Then one real question. Take a word the user's project surely knows (a project name, a folder, a class) and run:

```
$GRIMORA recall <word> --instance <instance>
```

If `recall` says "no confident answer", try `doc <word>` or `graph-query <word>`. A refusal is a correct answer, not an error; say which channel was empty. Report only what the commands printed.

## 5. Report

One short block:

- Instance name and store path (`<data folder>/<instance>/grimora.db`).
- The counts from `stats`.
- The first answer, quoted.
- What happens next: the hooks fold every session into the store at SessionEnd; `/mcp` shows the `grimora` server; the `grimora` skill tells Claude when to look things up. Nothing else is needed.

## Never

- Never run `init --full` a second time on the same instance without telling the user first.
- Never move or delete a data folder. Never copy a database without the user naming the path.
- Never write a token, password or key into the store or the report.
