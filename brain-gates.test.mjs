// The two read-side gates, driven as the harness drives them.
//
// Both once answered a live question out of memory. brain-gate DENIED a Grep and offered its FTS hits
// with "act on it and skip the search"; a session took them as the result, concluded some tests did not
// reference a timer, edited on that, and had to undo it. brain-read-gate served a directory synthesis
// for any prose file in that directory, including one added after the synthesis was written and so
// covered by no freshness check.
//
// A guard that substitutes memory for a live measurement cannot fail — it just reads as an answer.
// These cases exist so neither gate can quietly go back to doing it.
import { mkdirSync, writeFileSync, rmSync, existsSync } from 'node:fs';
import { execFileSync } from 'node:child_process';
import { homedir, tmpdir } from 'node:os';
import { join } from 'node:path';
import { DatabaseSync } from 'node:sqlite';

const hook = (name, payload, env = {}) => {
  const out = execFileSync('node', [join(import.meta.dirname, name)], {
    input: JSON.stringify(payload), encoding: 'utf8', env: { ...process.env, ...env },
  });
  return out ? (JSON.parse(out).hookSpecificOutput ?? {}) : {};
};

let pass = 0, fail = 0;
const check = (ok, label, why) => {
  console.log(`  ${ok ? 'ok  ' : 'FAIL'} ${label.padEnd(28)} ${why}`);
  ok ? pass++ : fail++;
};

// --- brain-gate: a search of the tree is never answered out of the store ---------------------------
{
  const seen = hook('brain-gate.mjs', {
    session_id: `bg-${process.pid}`,
    cwd: 'C:/Projects/NoMercy',
    tool_name: 'Grep',
    // Broad enough that the store is near-certain to match something, which is the risky case.
    tool_input: { pattern: 'encoder queue', path: 'C:/Projects/NoMercy/apps' },
  });
  check(seen.permissionDecision !== 'deny', 'grep-runs',
    'a Grep asks what the tree holds NOW; recall can never stand in for it');
  if (seen.additionalContext) {
    check(/never act on these rows in place of it/.test(seen.additionalContext), 'grep-recall-labelled',
      'recall must arrive marked as background, not as the result');
  }
}

// --- brain-read-gate: a synthesis answers only for the files it was derived from -------------------
{
  const INSTANCE = 'gatefixture';
  const home = join(homedir(), '.aitm', INSTANCE);
  const work = join(tmpdir(), INSTANCE);
  for (const p of [home, work]) if (existsSync(p)) rmSync(p, { recursive: true, force: true });
  mkdirSync(home, { recursive: true });
  mkdirSync(work, { recursive: true });

  try {
    writeFileSync(join(work, 'COVERED.md'), '# a source of the synthesis\n');
    writeFileSync(join(work, 'ADDED-LATER.md'), '# never one of its sources\n');

    const dir = work.replace(/\\/g, '/').toLowerCase();
    writeFileSync(join(home, 'synthesis.json'), JSON.stringify({ [dir]: true }));

    const db = new DatabaseSync(join(home, 'aitm.db'));
    db.exec('CREATE TABLE docs (k TEXT PRIMARY KEY, title TEXT, category TEXT, content TEXT)');
    // Stamped beyond any real mtime, so freshness cannot be what decides either case and the source
    // list is the only thing under test.
    db.prepare('INSERT INTO docs (k, title, category, content) VALUES (?, ?, ?, ?)').run(
      `synthesis:${dir}`, 'fixture synthesis', 'synthesis',
      'from: COVERED.md\nnewest_source_ticks: 999999999999999999\n\nthe distilled answer',
    );
    db.close();

    const read = (file, id) => hook('brain-read-gate.mjs', {
      session_id: `rg-${process.pid}-${id}`,
      cwd: work,
      tool_name: 'Read',
      tool_input: { file_path: join(work, file) },
    }, { CLAUDE_PROJECT_DIR: work }).permissionDecision;

    check(read('COVERED.md', 'a') === 'deny', 'synthesis-serves-source',
      'the distillation is what reading its own sources produces');
    check(read('ADDED-LATER.md', 'b') !== 'deny', 'synthesis-declines-stranger',
      'it cannot answer for a file it never read, and no mtime check covers one');
  } finally {
    for (const p of [home, work]) rmSync(p, { recursive: true, force: true });
  }
}

console.log(`\n${pass} passed, ${fail} failed`);
process.exit(fail ? 1 : 0);
