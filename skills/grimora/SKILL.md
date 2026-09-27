---
name: grimora
description: Find a NoMercy project fact, rule, past decision, cross-project impact, or known tool in Grimora; record a verified finding for future sessions. Use for project knowledge, not generic coding questions.
---

# Grimora project knowledge

Grimora is a shared knowledge store, one instance per workspace (`nomercy` in NoMercy). It holds verified facts, standing rules, absorbed docs, past conversations, and a cross-project code graph.

Its answers are leads with a source, not proof the current code still agrees. The source file and the running software are the real proof; a memory is context. Check the cited source before changing code or relying on a fact that matters. The user's current instruction and the target repo's own rules always outrank a stale entry.

A miss covers only the channel you asked. Before you conclude something is unknown, check another relevant channel or the owning repo. Never store raw secrets, bearer tokens, or a guess.

## Look up (read-only)

Call these before you state a project fact, write new code, or repeat a question already answered.

- `fact` — a verified project fact: base URL, file path, type/field name, config key, port, convention. Refuses instead of guessing.
- `rule` — a standing rule, preference, or past decision about how to work here.
- `recall` — what was said in a past conversation or session.
- `doc` — absorbed PRDs, plans, specs, and audits, by topic.
- `brain_core` — the always-on rows every turn should know. Pull this once at session start.
- `brain_scope` — everything in scope across two or more named projects: shared seams, contracts, and who exposes vs. who consumes. Call before any cross-project task.
- `brain_common` — what a set of named projects share in common.
- `brain_place` — where a new piece of code belongs and how it should be written, before you write it.
- `brain_recall` — the catch-all natural-language question when it is not clearly scope, common, or place.
- `brain_gaps` — what the brain could not answer recently. Check at session start or before research; an open gap you can now answer is worth staging.
- `history` — the change history for one entity, for tracing when something went wrong.
- `open_findings` — unrelated issues spotted earlier, still open.

## Impact (before changing a shared contract)

- `impact` — every cross-project consumer of a changing server symbol, endpoint, or SignalR event, flagging the hardcoded ones.
- `brain_impact` — the same question against the live edges graph plus the summary triple. Use either; both answer "what breaks if I change this."

Call one of these before renaming or reshaping anything another project depends on.

## Code graph (no external tool needed)

Grimora's own `edges` table IS the code graph — every declaration and curated usage site across every
registered project, one row per (symbol, file, line, project). These three tools read it directly, no
LLM, no separate index to keep in sync:

- `graph_query` — the most relevant symbols/files/docs for a natural-language question, one hop of
  neighbors (declaration file, top consuming projects), grouped by project. CLI: `grimora graph-query <question>`.
- `graph_path` — shortest path between two symbols/files over the code graph (declaration + usage
  edges), max depth 6, each hop printed with file:line. CLI: `grimora graph-path <A> <B>`.
- `graph_explain` — what a symbol is (kind, definition file:line), who uses it (grouped by project, top
  10 sites), and docs/rules that mention it. "What it uses" is reported as not tracked rather than
  guessed — there is no call-graph edge, only declaration/usage. CLI: `grimora graph-explain <symbol>`.

These replace the external `graphify` tool for this workspace: graphify's graph here only ever covered
the root repo's docs and scripts (nested repos — the actual product code — were never in it), so any
question about a real class, controller, or hub returned "no node matching" or a doc-only answer. Grimora's
edges table already spans every registered project because `index-code.mjs` walks them all.

Refresh the index after code changes: `node index-code.mjs [--instance <name>] [--project <name>]`
(idempotent — safe to run every session end; declarations only, so it stays cheap even on a big repo).

For a broad conceptual question (not a specific symbol), `doc` and `rule` still answer better than
`graph_query` — the code graph only knows declaration/usage sites, not narrative. Try the code graph
first for anything naming a class, controller, hub, or file; fall back to `doc`/`rule`/`brain_recall`
for "how does X work end to end" questions.

## Record (write)

Recording is a two-step commit, not a single call:

1. `brain_stage` — stage a learning the instant you notice it: a correction, a new fact, a convention, a contract, a URL or path. Stage aggressively — the moment something is confirmed wrong, empty, or new, not at turn end. Cheap, no DB write yet.
2. `brain_flush` — commit everything staged into the store in one pass, then clear the ledger.

Stage first, flush before you finish the turn. Nothing staged is ever silently dropped, but nothing staged is durable until flushed.

Other writes:
- `brain_learn` — the direct, single-call form of stage+commit for one learning, when you don't need the staging buffer.
- `shed_memory` — forget one rule/preference by its exact key, to retire something that no longer holds.
- `log_finding` — log an unrelated bug or issue spotted during other work, to surface to the operator later, not now.

Use the CLI `add` / `index-memory` commands instead if the MCP server is unavailable.

## Workspace tools

- `workspace_capabilities` — find an existing NoMercy tool by purpose (browser login, native code search, CI watching, process ownership) instead of writing a new one. Read-only discovery; does not run anything.
- `workspace_search` — search fixed text or filenames in one registered repository. Requires an explicit repo path (`.` means the root). File and line locations only, no source values, excludes credential paths. Search one repo at a time; never recurse the whole monorepo root.

## Auth (gated, rare)

- `idp_token` — mint a real IdP token for a subject, for automated test/API/SignalR calls. The only sanctioned way to authenticate for automated work; never weaken or bypass auth instead. Gated behind `GRIMORA_ALLOW_TOKEN_MINT=1`; when gated, run the sibling script directly instead of the tool.

## Rules

- Query before you guess. A refusal from `fact` or `rule` is not evidence the answer doesn't exist elsewhere — it is evidence this channel doesn't have it yet.
- Stage aggressively, flush before ending the turn. Re-deriving something twice costs more than writing it once.
- For a shared contract change, check `impact`/`brain_impact` AND read the current consumers yourself. The graph can lag a fast-moving repo.
- One repo at a time for `workspace_search`. Never point it at the whole monorepo root.
- Claude may also run Grimora hooks for indexing and pre-compaction saves. Other agents must make these reads and writes explicitly — do not assume a hook ran just because this skill is installed.
