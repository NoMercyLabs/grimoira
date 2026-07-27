// PreCompact hook: a summary is a lossy retelling, so the facts that must not be lost are written
// down BEFORE it happens rather than trusted to it.
//
// Two things leave here. Durable knowledge goes to the store, which outlives every summary and every
// session. Session anchors — branch, edited files, open todos, the directive being worked under — go
// to a brief that compact-restore.mjs injects back on the other side, because those are exactly what
// a summary paraphrases away and what the next turn then has to re-derive by reading files again.
import { readFileSync, writeFileSync, mkdirSync, existsSync } from 'node:fs';
import { homedir } from 'node:os';
import { join, dirname } from 'node:path';
import { execFileSync } from 'node:child_process';
import { resolveInstance, briefPath } from './brain-lib.mjs';

const readEntries = (tp) => {
  const out = [];
  for (const line of readFileSync(tp, 'utf8').split('\n')) {
    if (!line.trim()) continue;
    try { out.push(JSON.parse(line)); } catch { /* partial write */ }
  }
  return out;
};

const blocks = (entries) => entries.flatMap((e) => (Array.isArray(e.message?.content) ? e.message.content : []));

// The user's own words are the directive; a summary reliably keeps the task and drops the constraints.
function directives(entries) {
  const said = [];
  for (const e of entries) {
    if (e.type !== 'user' || e.isMeta) continue;
    const c = e.message?.content;
    const text = typeof c === 'string' ? c : Array.isArray(c) ? c.filter((b) => b.type === 'text').map((b) => b.text).join(' ') : '';
    const trimmed = text.trim();
    if (trimmed.length < 12 || trimmed.startsWith('<') || trimmed.startsWith('Caveat:')) continue;
    // An earlier compaction's summary is injected as a user turn. Quoting it back as a directive would
    // let each compaction copy the previous one forward until the brief is nothing but old summaries.
    if (/^(This session is being continued|Caveat: The messages below|\[Request interrupted)/.test(trimmed)) continue;
    if (/hook (feedback|additional context)/i.test(trimmed.slice(0, 60))) continue;
    said.push(trimmed.replace(/\s+/g, ' ').slice(0, 400));
  }
  return said.slice(-4);
}

// Scratch files are deliberately throwaway; carrying them across a compaction makes the real edits
// harder to see and invites the next turn to treat a temp script as project work.
const SCRATCH = /(^|[\\/])(scratchpad|\.scratch|Temp|tmp)([\\/]|$)/i;

function touchedFiles(entries) {
  const files = new Set();
  for (const b of blocks(entries)) {
    if (b.type !== 'tool_use') continue;
    if (!['Edit', 'Write', 'NotebookEdit'].includes(b.name)) continue;
    if (b.input?.file_path && !SCRATCH.test(b.input.file_path)) files.add(b.input.file_path);
  }
  return [...files];
}

function openTodos(entries) {
  let todos = [];
  for (const b of blocks(entries)) {
    if (b.type === 'tool_use' && b.name === 'TodoWrite' && Array.isArray(b.input?.todos)) todos = b.input.todos;
  }
  return todos.filter((t) => t.status !== 'completed');
}

// Which repos still hold work, and on what branch. "Which branch am I on" is a HARD rule here and a
// summary never carries it, so it gets re-asked or, worse, assumed.
function repoState(files) {
  const roots = new Set();
  for (const f of files) {
    let dir = dirname(f.replace(/\\/g, '/'));
    for (let i = 0; i < 8 && dir.length > 3; i++) {
      if (existsSync(join(dir, '.git'))) { roots.add(dir); break; }
      dir = dirname(dir);
    }
  }
  const out = [];
  for (const root of roots) {
    try {
      const branch = execFileSync('git', ['-C', root, 'branch', '--show-current'], { encoding: 'utf8' }).trim();
      const dirty = execFileSync('git', ['-C', root, 'status', '--porcelain'], { encoding: 'utf8' }).trim();
      const head = execFileSync('git', ['-C', root, 'log', '-1', '--format=%h %s'], { encoding: 'utf8' }).trim();
      out.push({ root, branch, dirty: dirty ? dirty.split('\n').length : 0, head });
    } catch { /* not a repo, or git unavailable */ }
  }
  return out;
}

let input = '';
process.stdin.on('data', (d) => { input += d; });
process.stdin.on('end', () => {
  try {
    const payload = JSON.parse(input || '{}');
    const tp = payload.transcript_path;
    if (!tp || !existsSync(tp)) process.exit(0);

    const instance = resolveInstance(payload);
    const entries = readEntries(tp);
    const files = touchedFiles(entries);
    const todos = openTodos(entries);
    const repos = repoState(files);
    const said = directives(entries);

    const lines = ['# Carried across compaction', ''];
    if (said.length) {
      lines.push('## What the owner actually asked for, in his words', '');
      for (const s of said) lines.push(`- "${s}"`);
      lines.push('');
    }
    if (repos.length) {
      lines.push('## Repo state at compaction', '');
      for (const r of repos) {
        lines.push(`- ${r.root} — branch \`${r.branch}\`, HEAD ${r.head}${r.dirty ? `, ${r.dirty} uncommitted path(s)` : ', clean'}`);
      }
      lines.push('');
    }
    if (files.length) {
      lines.push('## Files this session changed', '');
      for (const f of files.slice(0, 40)) lines.push(`- ${f}`);
      if (files.length > 40) lines.push(`- … ${files.length - 40} more`);
      lines.push('');
    }
    if (todos.length) {
      lines.push('## Still open', '');
      for (const t of todos) lines.push(`- [${t.status}] ${t.content}`);
      lines.push('');
    }
    const brief = lines.join('\n');

    const out = briefPath(instance, payload.session_id);
    try { mkdirSync(dirname(out), { recursive: true }); writeFileSync(out, brief); } catch { /* best effort */ }

    // PLAIN TEXT, not a hookSpecificOutput envelope. PreCompact collects each hook's raw stdout; the
    // envelope every other hook event takes fails schema validation here, which is how this hook spent
    // its first real compaction printing an error instead of saving anything.
    process.stdout.write(
      'The summary must carry these verbatim, not paraphrased: the exact wording of any standing ' +
      'directive, every file path already changed, the branch each repo is on, what is committed versus ' +
      'still uncommitted, and every unfinished item. Concrete anchors survive compaction; impressions ' +
      `do not.\n\n${brief}`
    );
  } catch {
    // fail open — a compaction must never be blocked by bookkeeping
  }
  process.exit(0);
});
