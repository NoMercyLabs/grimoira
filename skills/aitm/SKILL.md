---
name: aitm
description: Find a NoMercy project fact, prior decision, rule, or known tool in AITM; record a verified finding that future sessions need. Use for project knowledge, not generic coding questions.
---

# AITM project knowledge

AITM is a shared knowledge store. Its answers are leads with provenance, not proof that the current source still agrees. Check the cited source before changing code or relying on a consequential fact. The user's current instruction and the target repository's rules take priority over stale entries.

Use the connected MCP server when available. The corresponding CLI is `aitm <command> --instance <name>`; run the built CLI from the AITM checkout if it is not on PATH. In NoMercy the instance is `nomercy`.

| Need | MCP tool | CLI command |
| --- | --- | --- |
| Verified project fact | `fact` | `query` |
| Standing rule or preference | `rule` | `mem` |
| Earlier conversation | `recall` | `recall` |
| Indexed design or plan | `doc` | `doc` |
| Known symbol consumers | `impact` | `impact` |
| Existing NoMercy tool | `workspace_capabilities` | `python scripts/workspace-capabilities.py "task"` |
| Scoped source locations | `workspace_search` | `python scripts/workspace-search.py --repo REPO --pattern TEXT` |

A miss covers only the channel queried. Check another relevant channel or the owning repository before concluding that a fact or tool does not exist. Search one registered repository at a time; never recursively search the whole NoMercy root. For a shared contract change, inspect current consumers as well as AITM's impact result.

Record a durable correction or verified decision with its source using `brain_stage`, then `brain_flush`. Use the CLI `add` command if MCP is unavailable. Do not store raw secrets, bearer tokens, transient command output, or guesses. Keep one clear fact per entry and retire or supersede stale guidance instead of adding a conflicting copy.

Claude may also run AITM hooks for recall and capture. Other agents must make these reads and writes explicitly unless their own integration provides equivalent hooks. Do not assume a hook ran because this skill is installed.
