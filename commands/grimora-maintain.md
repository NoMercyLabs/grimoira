---
name: grimora-maintain
description: Run an Grimora CLI admin/maintenance verb (seed-edges, promote-all, shed-doc/fact/memory/node/synthesis, spine-export/import, forget-project) and report the result.
---

# /grimora-maintain

Run one Grimora CLI admin verb on request. These are occasional, deliberate maintenance operations on the knowledge store — never run automatically, and never more than one verb per invocation unless the user explicitly lists several.

Argument: `$ARGUMENTS` — the verb name, plus whatever flags that verb needs.

## Before running anything

1. Locate the built CLI. As an installed plugin it is `<config>/plugins/data/grimora-<marketplace>/current/bin-cli/grimora.dll`, where `<config>` is `$CLAUDE_CONFIG_DIR` or `~/.claude` (glob `grimora-*` when the marketplace name is unknown). In a source checkout it is `bin-cli/grimora.dll` under the repo root; if that is missing, run `./build-cli.sh` (or `build-cli.ps1`) first. If the installed `current` folder is missing, the background build after an install or update has not finished: say so and stop.
2. Resolve the instance the same way every other Grimora entry point does: an explicit `--instance` the user names, else `GRIMORA_INSTANCE`, else the current workspace's folder name. Do not hardcode a default instance — a different workspace has a different one.
3. If the requested verb is destructive (`shed-*`, `forget-project`) and the user gave no explicit key/name/path, ask for it — do not guess an argument that deletes data.

## Supported verbs

| Verb | Flags | Does |
| --- | --- | --- |
| `seed-edges` | none | Sync the curated cross-project edge seed. |
| `promote-all` | `--symbol <s>` | Authoritative replace of one symbol's edges from reviewed candidates. |
| `shed-doc` | `--path <substring>` | Drop absorbed doc sections whose path matches. |
| `shed-fact` | `--key <term>` | Forget one fact by its key. |
| `shed-memory` | `--key <slug>` | Forget one rule/preference by its key. |
| `shed-node` | `--key <k>` | Retire a node and its links. |
| `shed-synthesis` | `--path <source dir>` | Drop synthesis rows absorbed from one source directory. |
| `spine-export` | `[--to <file>]` | Dump the curated spine to JSON, for moving curated knowledge to another instance. |
| `spine-import` | `--from <file>` | Load a curated spine from JSON. |
| `forget-project` | `--name <n>` | Unregister a project and drop its edges. |

## Running it

Build the command as:

```
dotnet <path to grimora.dll> <verb> --instance <resolved instance> [flags from the table above]
```

Run it, capture stdout/stderr, and report:
- The exact command run.
- Whether it exited 0.
- The tool's own output (row counts, before/after, or the error message).

A `shed-*` or `forget-project` verb changes the store irreversibly — after running one, suggest `grimora stats --instance <resolved instance>` so the user can see the effect, rather than assuming it worked from exit code alone.

If `$ARGUMENTS` names a verb not in this table, say so and list the supported verbs instead of guessing a flag shape.
