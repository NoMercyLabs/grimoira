// The guard driven end to end over the pinned corpus. deferral-shape.test.mjs measures the same cases
// against the classifier in isolation; this one proves the wiring around it — transcript reading,
// real-blocker suppression, the AskUserQuestion escape — because a classifier that decides correctly
// inside a hook that reads the transcript wrongly is a hook that never fires.
//
// The corpus lives in deferral-corpus.mjs. It used to be inline here, which meant the two
// implementations measured themselves against two copies of it, and a corpus that exists twice is a
// corpus that disagrees with itself.
import { writeFileSync, mkdirSync, rmSync } from 'node:fs';
import { execFileSync } from 'node:child_process';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { CASES } from './deferral-corpus.mjs';

const dir = join(tmpdir(), `cg-test-${process.pid}`);
mkdirSync(dir, { recursive: true });

function fires(finalText, id) {
  const tp = join(dir, `${id}.jsonl`);
  writeFileSync(tp, [
    JSON.stringify({ type: 'user', message: { role: 'user', content: 'do the thing' } }),
    JSON.stringify({ type: 'assistant', message: { role: 'assistant', content: [{ type: 'text', text: finalText }] } }),
  ].join('\n') + '\n');
  const out = execFileSync('node', [join(import.meta.dirname, 'continue-guard.mjs')], {
    input: JSON.stringify({ session_id: `cg-${id}`, transcript_path: tp, cwd: process.cwd() }),
    encoding: 'utf8',
  });
  return out.trim().length > 0 && JSON.parse(out).decision === 'block';
}

let pass = 0, fail = 0;
for (const [id, want, text] of CASES) {
  const got = fires(text, id);
  const ok = got === want;
  console.log(`  ${ok ? 'ok  ' : 'FAIL'} ${id.padEnd(22)} expected ${want ? 'block' : 'quiet'}, got ${got ? 'block' : 'quiet'}`);
  ok ? pass++ : fail++;
}

// A genuine capability limit is not deferral: there is nothing to continue with, so the guard must
// stand aside rather than demand work that cannot be done.
const blocked = fires(
  'I cannot access the IdP admin console without you logging the session in, so the realm export '
  + 'is where it stops. Everything else in the slice is committed.', 'real-blocker');
console.log(`  ${!blocked ? 'ok  ' : 'FAIL'} ${'real-blocker'.padEnd(22)} expected quiet, got ${blocked ? 'block' : 'quiet'}`);
!blocked ? pass++ : fail++;

// The no-ledger branch: substantive work (>=3 edits) with no todo list anywhere must block, so a
// silent premature stop can't slip past a guard that only knows deferral phrasing.
function firesWith(assistantBlocks, id, extraUser) {
  const tp = join(dir, `${id}.jsonl`);
  const rows = [JSON.stringify({ type: 'user', message: { role: 'user', content: 'do the thing' } })];
  if (extraUser) rows.push(JSON.stringify(extraUser));
  rows.push(JSON.stringify({ type: 'assistant', message: { role: 'assistant', content: assistantBlocks } }));
  writeFileSync(tp, rows.join('\n') + '\n');
  const out = execFileSync('node', [join(import.meta.dirname, 'continue-guard.mjs')], {
    input: JSON.stringify({ session_id: `cg-${id}`, transcript_path: tp, cwd: process.cwd() }),
    encoding: 'utf8',
  });
  return out.trim().length > 0 && JSON.parse(out).decision === 'block';
}
const edit = (f) => ({ type: 'tool_use', name: 'Edit', input: { file_path: f } });
const doneText = { type: 'text', text: 'Done, shipped.' };
const todoWrite = (todos) => ({ type: 'tool_use', name: 'TodoWrite', input: { todos } });

const ledgerCases = [
  ['no-ledger-3-edits', true, [edit('a.ts'), edit('b.ts'), edit('c.ts'), doneText], null],
  ['no-ledger-2-edits', false, [edit('a.ts'), edit('b.ts'), doneText], null],
  ['ledger-all-done', false, [todoWrite([{ content: 'x', status: 'completed', activeForm: 'x' }]), edit('a.ts'), edit('b.ts'), edit('c.ts'), doneText], null],
];
for (const [id, want, blocks, extraUser] of ledgerCases) {
  const got = firesWith(blocks, id, extraUser);
  const ok = got === want;
  console.log(`  ${ok ? 'ok  ' : 'FAIL'} ${id.padEnd(22)} expected ${want ? 'block' : 'quiet'}, got ${got ? 'block' : 'quiet'}`);
  ok ? pass++ : fail++;
}

// Writing tests and reporting them green is the shape this catches: a green test proves nothing until
// something was seen to fail, and "28 green" reads as the whole surface unless the remainder is counted.
const testCases = [
  ['tests-no-red-no-fraction', true,
    [edit('quality.test.ts'), { type: 'text', text: 'Added three tests. Suite is 28 green.' }]],
  ['tests-red-no-fraction', true,
    [edit('quality.test.ts'), { type: 'text', text: 'Each proven red by its own mutation. 28 green.' }]],
  ['tests-fraction-no-red', true,
    [edit('quality.test.ts'), { type: 'text', text: 'Added tests. LiveSourcePlugin (131 lines) remains unpinned.' }]],
  ['tests-red-and-fraction', false,
    [edit('quality.test.ts'), { type: 'text', text: 'Each proven red by its own mutation: ids collapsed to zero. Remaining unpinned: LiveSourcePlugin (131 lines), SyncPlugin (161).' }]],
  ['non-test-edit-untouched', false,
    [edit('player.ts'), { type: 'text', text: 'Done, shipped.' }]],
];
for (const [id, want, blocks] of testCases) {
  const got = firesWith(blocks, id, null);
  const ok = got === want;
  console.log(`  ${ok ? 'ok  ' : 'FAIL'} ${id.padEnd(26)} expected ${want ? 'block' : 'quiet'}, got ${got ? 'block' : 'quiet'}`);
  ok ? pass++ : fail++;
}

try { rmSync(dir, { recursive: true, force: true }); } catch { /* best effort */ }
console.log(`\n${pass} passed, ${fail} failed`);
process.exit(fail === 0 ? 0 : 1);
