---
name: aitm
description: Use when you need a ground-truth project fact (file path, symbol location, API shape, config key, convention, past decision) or when you have just established one worth keeping. Query the store before asserting any project-specific fact, and write findings back the same turn you learn them.
---

# aitm

A queryable store of what is actually true about this project, kept separately from the model's prior. It exists because markdown rule files are advisory text an agent can rationalise past, so project facts get asserted from training data and cross-project contracts break silently.

**The store is the authority on project facts. Your prior is not.**

## Read before asserting

Reach the CLI at `aitm <cmd> --instance <name>` (instance defaults to the project directory name).

| Question | Command |
| --- | --- |
| A verified fact (paths, shapes, keys, versions) | `query <terms>` |
| A durable rule, preference, or past decision | `mem <terms>` / `mem --hard` for always-on rules |
| Something said in an earlier session | `recall <terms>` |
| Spec, plan, or design doc content | `doc <terms>` |
| Where a symbol lives and who consumes it | `impact <symbol>` |
| What the store knows it does not know | `brain_gaps` / the `gaps` table |

If a lookup returns nothing, that is an answer too: the store does not know, so the filesystem is the next stop and whatever you find there is worth recording.

## Write the same turn you learn

The read path is automatic; the write path is the one that decays. A fact established and not written down is re-derived from scratch next session.

```
aitm add --term "<short handle>" --category <cat> --value "<the finding, stated as a fact>" --source "<where it came from>"
```

State the finding, not the search that found it. "Encoder stream-copies when source codec matches the target" is durable; "grepped for smart copy" is not.

Rules and preferences belong in the memory channel via `index-memory`; docs via `index-docs`; the code graph refreshes itself via `index-code.mjs`.

## What runs on its own

- **`brain-gate`** (before Grep/Glob) puts the search to the store first. If the store answers, the search is denied and the hits come back instead. Read them. If they genuinely do not answer the question, re-issue the identical search and it runs; the gate fires once per search per session.
- **`brain-harvest`** (after Grep/Glob) writes whatever the search found back into the code graph.
- **`brain-capture`** (at stop) blocks once if the session opened gaps and taught none of them back.

None of these replace judgement. They make the default path the correct one.

## Store notes

Node's bundled SQLite has no FTS5, so anything touching an `*_fts` table must go through the C# CLI or the store and its search index drift apart silently. The store runs in WAL mode; readers are never blocked by the long indexers.
