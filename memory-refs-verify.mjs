#!/usr/bin/env node
// Does every branch and repo path the memory store names actually exist?
//
// Why this exists: 25 memory files instructed against `feat/encoder-v3`, a branch that had not
// existed for months — and a subagent, asked to check, reported that it DID exist. Rules were being
// followed against a fiction, and two other memories pointed at scripts/local-ci.sh and
// scripts/run-tests.sh with the wrong prefix, sending an agent hunting for files that were really one
// directory deeper. A memory that names something unreachable is worse than no memory: it is
// confident, wrong, and it teaches the next agent to invent.
//
// So the store gets the same treatment as code — a check that RUNS. Branch names and repo-relative
// paths are resolved against the real filesystem and the real branch lists; anything that does not
// resolve is printed with the files that cite it.
//
//   node memory-refs-verify.mjs            report only, exit 0
//   node memory-refs-verify.mjs --strict   exit 1 when anything is unresolved (for a gate)
//
// Placeholders (feat/<name>, fix/..., globs) are skipped: they teach a pattern, they don't claim a fact.

import { readdirSync, readFileSync, existsSync } from 'node:fs';
import { execSync } from 'node:child_process';
import { join } from 'node:path';
import { WORKSPACE, registeredRepos } from './workspace-repos.mjs';

const MEM = 'C:/Users/dev/.claude/projects/c--Projects-NoMercy/memory';
const ROOT = WORKSPACE;
// From the workspace registry, so a layout move never leaves this checker reading folders that are gone.
const REGISTERED = registeredRepos(ROOT);
const REPOS = ['.', ...REGISTERED];
const TOP_FOLDERS = [...new Set([...REGISTERED.map((repo) => repo.split('/')[0]), '.claude', 'scripts'])];

const BRANCH = /\b(?:feat|fix|feature|release|hotfix|db-fix|refactor)\/[A-Za-z0-9._-]+/g;
const PATHREF = new RegExp(`\\b(?:${TOP_FOLDERS.map((folder) => folder.replace('.', '\\.')).join('|')})\\/[A-Za-z0-9._\\-/]+`, 'g');
// A placeholder teaches a shape; only a concrete name is a factual claim.
const PLACEHOLDER = /\/(?:<|\.\.\.|x$|name|thing|topic|slug|target|feature|branch|concern|bonus|extras|test|format|debug|fix)$/i;

function branches() {
  const all = new Set();
  for (const r of REPOS) {
    try {
      execSync(`git -C "${join(ROOT, r)}" branch -a --format="%(refname:short)"`,
        { encoding: 'utf8', stdio: ['ignore', 'pipe', 'ignore'] })
        .split('\n').map((s) => s.trim()).filter(Boolean)
        .forEach((b) => all.add(b.replace(/^origin\//, '')));
    } catch { /* not a repo */ }
  }
  return all;
}

// Every place a repo-relative path could legitimately be rooted: the monorepo itself, every nested
// project under it, and the two sibling repos outside it. Enumerated from disk rather than listed by
// hand — a hardcoded list is the thing that goes stale and makes this checker lie in its own way.
const ROOTS = [ROOT, 'C:/Projects/aitm', 'C:/Projects/aaoa-dev/Mom-icons', ...REGISTERED.map((repo) => join(ROOT, repo))];
const resolvesAnywhere = (p) => ROOTS.some((r) => existsSync(join(r, p)));

const known = branches();
const deadBranch = new Map();
const deadPath = new Map();
const note = (map, key, file) => map.set(key, [...(map.get(key) || []), file]);

for (const file of readdirSync(MEM).filter((f) => f.endsWith('.md'))) {
  const text = readFileSync(join(MEM, file), 'utf8');
  for (const b of new Set(text.match(BRANCH) || [])) {
    if (PLACEHOLDER.test(b) || known.has(b)) continue;
    note(deadBranch, b, file);
  }
  for (const raw of new Set(text.match(PATHREF) || [])) {
    const p = raw.replace(/[.,)\]:]+$/, '');
    if (/[<>*?]|\.\.\./.test(p) || p.split('/').length < 2) continue;
    if (p.endsWith('/nomercy-') || p.endsWith('-')) continue;   // truncated by the regex, not a claim
    // A memory often writes a path relative to the repo it is about ("scripts/run-tests.sh"), so try
    // each nested repo before calling it dead — otherwise the report is mostly false alarms, and a
    // tool that cries wolf is one nobody runs.
    if (resolvesAnywhere(p)) continue;
    note(deadPath, p, file);
  }
}

const show = (title, map) => {
  console.log(`\n=== ${title} (${map.size}) ===`);
  for (const [k, files] of [...map].sort()) {
    const cited = files.slice(0, 3).join(', ') + (files.length > 3 ? ` +${files.length - 3}` : '');
    console.log(`  ${k}\n      cited by: ${cited}`);
  }
  if (map.size === 0) console.log('  (none — every reference resolves)');
};

show('BRANCHES named in memories that do not exist', deadBranch);
show('PATHS named in memories that do not exist', deadPath);

const total = deadBranch.size + deadPath.size;
console.log(`\n${total} unresolved reference(s).`);
if (process.argv.includes('--strict') && total > 0) process.exit(1);
