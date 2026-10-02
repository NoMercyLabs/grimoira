# How Grimoira works with Claude Code

This page is for you if you are deciding whether to adopt Grimoira. It says what Grimoira changes in Claude Code, what it stores, what it costs, and how to turn it off. It also says what Grimoira does not do. For install steps, configuration and troubleshooting, see the [README](README.md). For how the code is laid out, see [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).

## 1. What it is for

A Claude Code session forgets everything when it ends. The next session finds the same facts again and writes the same commands again. It may also repeat a mistake you already corrected.

Grimoira is a store that sits behind Claude Code. It keeps the facts, rules, decisions, todos, findings and code relationships that sessions produce. Claude asks the store before it guesses. Hooks keep the store current and save your place before a conversation is compacted. The goal is that work is done once and then found again, not redone.

Grimoira is built to be Claude Code's memory and knowledge layer. Once installed, it takes part in every session.

## 2. What it changes in Claude Code

Grimoira is a Claude Code plugin. It adds six hooks, one MCP server, two skills, two agents and one slash command. It also adds a `grimoira` command for your terminal.

### Hooks

Every hook fails open. A hook error prints nothing and never blocks a prompt, an edit or a session end. No hook can refuse a tool call.

| Event | Time limit | What it does |
| --- | --- | --- |
| `SessionStart` | 15 s | Starts the background service if it is not answering. After an install or update it starts a background build instead and prints one line saying so. |
| `UserPromptSubmit` | 10 s | Adds the anchors saved before the last compaction to the first prompt after it, once. Otherwise adds nothing. |
| `PostToolUse` (Write, Edit, MultiEdit, NotebookEdit) | 65 s | Reindexes the memory or design-docs folder when the edited file is inside one. Any other edit does nothing. |
| `PreCompact` | 20 s | Writes your own words, dirty repos and branches, changed files and open todos to disk. Tells the summary to keep them word for word. |
| `SessionEnd` | 390 s, async | Queues background indexing: the session transcript, the project's `.claude/` docs and the code declarations of every registered project. |
| `Stop` | 10 s | If staged learning is waiting and was not committed, commits it and says so. If it cannot, it prints a warning. It does not stop the session from ending. |

The service also has a handler that counts repeated Bash and PowerShell command shapes. `hooks/hooks.json` does not register it today, so it does not run.

### MCP server

The MCP server exposes 26 tools. They are grouped like this:

| Group | Tools |
| --- | --- |
| Facts, todos, findings | `fact`, `log_finding`, `open_findings` |
| Rules | `rule`, `shed_memory` |
| Chat history | `recall`, `chat_list`, `chat_count` |
| Docs | `doc` |
| Code graph | `impact`, `graph_query`, `graph_path`, `graph_explain` |
| Brain (cross-project) | `brain_core`, `brain_scope`, `brain_common`, `brain_place`, `brain_recall`, `brain_impact`, `brain_gaps`, `brain_learn`, `brain_stage`, `brain_flush` |
| History | `history` |
| Workspace | `workspace_capabilities`, `workspace_search` |

### Skills, agents and the command

- Skill `grimoira`: tells Claude when to look something up and when to record something.
- Skill `grimoira-init`: the first-start setup. It asks three questions and runs the setup.
- Agent `knowledge-lookup`: read-only answers from the store.
- Agent `knowledge-writer`: after a task, decides what is worth recording, then stages and flushes it.
- Command `/grimoira-maintain`: occasional admin work, such as forgetting a fact or a project. It is never run automatically.

### The `grimoira` command

`grimoira help` lists every verb. The hooks and the MCP server both run this command.

## 3. The read and write loop

Here is one session, in order.

1. **Session start.** The `SessionStart` hook makes sure the service is running. The `grimoira` skill tells Claude to pull the always-on rows (`brain_core`) and to check `brain_gaps` at the start.
2. **Before Claude states a fact or writes new code.** The skill tells Claude to call `fact`, `rule`, `recall` or `doc` first. Before changing something other projects use, it tells Claude to call `impact`. If the store does not know, it says so and logs the gap. It does not guess.
3. **When Claude learns something.** The skill tells Claude to stage it with `brain_stage` at once. Staging writes to a ledger file, not to the store.
4. **After an edit.** The `PostToolUse` hook reindexes memory and design docs when the edited file is one of those.
5. **Before a compaction.** `PreCompact` saves your words and the open state to disk. The first prompt after the compaction gets them back through `UserPromptSubmit`.
6. **When Claude finishes a turn.** The `Stop` hook commits any staged learning that is still waiting.
7. **When the session ends.** `SessionEnd` indexes the transcript, the project docs and the code declarations in the background.

### What is enforced and what is advised

Be clear about this part.

- **Enforced by hooks:** the service starts, the compaction anchors are saved and handed back, staged learning is committed at `Stop`, and indexing happens at `SessionEnd` and after memory or design-doc edits. These run whether or not Claude remembers to do them.
- **Advised by the skill:** looking things up before guessing, staging a learning when you notice it, checking impact before a shared change. Claude does these because the skill says so. No hook checks that it did. The skill says this itself: do not assume a hook ran just because the skill is installed.
- **Not built:** a hook that runs before an edit or a tool call. Grimoira does not show rules before a governed edit, and it does not block anything.

## 4. Why nothing is forgotten

### What is stored

One SQLite file per instance holds these channels:

- Facts: verified values such as a URL, a path, a config key or a port.
- Rules: standing rules, preferences and past decisions.
- Findings and todos.
- A code graph: declarations and usage sites for registered projects.
- Absorbed docs: plans, specs and docs, chunked by section.
- A chat index: user messages, assistant replies, pasted content and results of reading untracked files.
- A cross-project brain: nodes, links and the gaps it could not answer.
- A change history of the store itself.

### How recall finds it

You ask by topic or by symbol. `fact`, `rule`, `doc` and `recall` return a few ranked hits with a source. `graph_query`, `graph_path` and `graph_explain` read the code graph. `chat_list` and `chat_count` list and count past conversation turns with totals. A miss is logged as a gap, so you can see what the store could not answer.

### The limits

- Grimoira stores a code graph of declarations and usage sites. It does not store your source code. It has no call graph: "what it uses" is reported as not tracked.
- A learning is durable only after it is flushed. Staged entries are committed by `Stop` and by the `knowledge-writer` agent, and `Stop` warns when it cannot.
- Nothing decides for Claude that something is worth recording. The skill and the agent advise it. A fact that nobody staged is not in the store.
- The code graph covers projects you registered. It can lag a repo that changes fast, so read the consumers yourself before a shared change.
- Answers are leads with a source. The source file and the running code are the proof.
- Chat history is indexed at `SessionEnd`, or by hand with `grimoira index-chat`. A session that never ends cleanly may be missing until you index it.
- Before conversation text is stored, token-shaped strings are replaced with a redaction marker. This covers JWTs, bearer tokens, common key prefixes and PEM private keys. It is pattern matching, so it does not catch every secret.

## 5. Why work becomes more repeatable

Grimoira makes the same question return the same answer from the store, instead of a new guess each time.

- **Code graph and impact.** `impact`, `brain_impact`, `graph_query`, `graph_path` and `graph_explain` read the same indexed declarations and usage sites, with file and line. Two sessions asking "who uses this symbol" read the same rows.
- **Facts and rules.** A fact or rule is looked up by key or topic. A miss is an explicit refusal, not an invented answer.
- **Placement.** `brain_place` answers where a new piece of code belongs and how it is written in this project.
- **Repeated work.** The service can count repeated Bash and PowerShell command shapes and the `patterns` tool lists the ones worth codifying. This is not active today: the hook that records them is not registered in `hooks/hooks.json`, and the MCP server does not expose `patterns`. Treat it as not available.
- **Rules before an edit.** Not built. Rules are returned when Claude asks for them, not pushed before an edit.

This makes the inputs consistent. It does not make Claude's output deterministic. Claude still decides what to ask and what to do with the answer.

## 6. Where your data lives and what leaves your machine

- **Store:** `~/.grimoira/<instance>/grimoira.db`, on every operating system. Set `GRIMOIRA_DATA_DIR` to use another folder.
- **Instance:** one store. Its name comes from `GRIMOIRA_INSTANCE`, or from the folder of the project Claude is working in.
- **Compaction files:** the saved briefs and ledgers sit in the instance folder under `compact/`.
- **Built command and service:** kept in the plugin's own data folder, in Claude Code's config folder.
- **Network:** the service listens only on a local named pipe on Windows, or a Unix socket on macOS and Linux, limited to the current user. No network port is open. On macOS and Linux the data folder is set to user-only permissions.
- **Telemetry:** none found. The source has no analytics or telemetry code.
- **Outbound calls:** the hooks, the MCP server and the service talk to each other over the local pipe or socket. Grimoira runs `git` locally to read repo state for the compaction brief. No code path that sends your store to another machine was found.

## 7. Cost

- **Disk:** the store grows with what you index. The size is not measured. A first-time copy from an older Grimora or AITM store needs free space equal to that store.
- **Runtime:** the .NET 10 SDK. The plugin builds its own command and service with it. Nothing else is required.
- **Build time:** the first session after an install or an update has no built command yet. The build runs in the background and takes a few minutes. Your session works in the meantime, without Grimoira.
- **Background service:** it starts on the first call and exits after 30 quiet minutes. Memory use is not measured.
- **Startup time per hook:** not measured. The hooks have time limits, listed in section 2.
- **Tokens:** not measured. Lookups add the answer text to the context. They can replace a search that would read files instead. The balance depends on your work and has not been measured.

## 8. Control

- **See what is stored:** `grimoira stats` shows channel counts. `grimoira projects` lists registered projects. `grimoira query`, `mem`, `doc` and `recall` read each channel. `grimoira history <term>` shows the change history of one entity. `grimoira todos` and `findings` list those. The SQLite file can also be opened with any SQLite tool.
- **Delete something:** `shed-fact --key`, `shed-memory --key` (also the `shed_memory` tool), `shed-doc --path`, `shed-node --key`, `shed-synthesis --path` and `forget-project --name`. `/grimoira-maintain` runs these and asks first when no key is given.
- **Correct something:** there is no edit verb in this list. Delete the entry, then add the right one with `add`, `brain_learn` or `brain_stage`.
- **Pause the hooks:** not built. Grimoira has no switch of its own to turn off one hook or all of them. `grimoira hooks-doctor` only finds duplicate hook registrations. You can remove the plugin from the `/plugin` menu.
- **Uninstall:** remove the plugin from the `/plugin` menu. Your data stays in `~/.grimoira` until you delete that folder yourself.
- **Export and move:** `spine-export` and `spine-import` move curated knowledge to another instance. `import --from <db>` merges another store.

## 9. Setup

In Claude Code:

```
/plugin marketplace add NoMercyLabs/skills
/plugin install grimoira@nomercylabs
```

`NoMercyLabs/skills` is the one install list for every NoMercy Labs plugin. If you already added `NoMercyLabs/grimoira`, keep it: it lists the same plugins.

Then ask Claude to "set up grimoira". The `grimoira-init` skill asks where the store lives, which projects to index and what to import. It runs the setup and proves the first answer with a real question.

You need the .NET 10 SDK. Start a new session after the first build has finished, which takes a few minutes.
