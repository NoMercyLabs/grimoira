// Both detectors, driven the two ways that can diverge: the decision function directly, and the whole
// hook over a synthetic transcript. The second matters because a guard that decides correctly and
// reads the transcript wrongly is a guard that never fires — the reply is not the effect.
import { writeFileSync, mkdirSync, rmSync } from 'node:fs';
import { execFileSync } from 'node:child_process';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { verdict, userText, toolUse } from './population-guard.mjs';

let pass = 0, fail = 0;
const check = (ok, label, detail) => {
  console.log(`  ${ok ? 'ok  ' : 'FAIL'} ${label}`);
  if (!ok && detail) console.log(`       ${detail}`);
  ok ? pass++ : fail++;
};

// ---------------------------------------------------------------- detector A: fixed the instance
// Verbatim shape of a real report: one element named, one file touched, no search anywhere.
const bugReport = 'the heading and intro does not gain any scroll or highlight focus when its spoken '
  + 'about... so this is a structural error `AudioSync.ts`';

check(verdict({ prompt: bugReport, finalText: 'Fixed.', edited: ['AudioSync.ts'], searched: false }).length === 1,
  'a defect report fixed at the named anchor with no search blocks');

check(verdict({ prompt: bugReport, finalText: 'Fixed.', edited: ['AudioSync.ts'], searched: true }).length === 0,
  'the same turn with a repo search stays quiet');

check(verdict({
  prompt: bugReport, finalText: 'Fixed.', searched: false,
  edited: ['a.ts', 'b.ts', 'c.ts', 'd.ts', 'e.ts'],
}).length === 0, 'five files touched is plainly a sweep, search evidence or not');

check(verdict({ prompt: bugReport, finalText: 'Fixed.', edited: [], searched: false }).length === 0,
  'answering a question without editing anything is not an instance fix');

check(verdict({
  prompt: 'add a dark mode toggle to `Settings.vue`', finalText: 'Added.',
  edited: ['Settings.vue'], searched: false,
}).length === 0, 'a feature request is not a defect report');

// A screenshot with no code span is still a concrete anchor — most of his bug reports look like this.
check(verdict({
  prompt: 'this is broken [screenshot] nothing happens when I click it',
  finalText: 'Fixed.', edited: ['Button.kt'], searched: false,
}).length === 1, 'a screenshot counts as the anchor');

// ------------------------------------------------------- detector B: done without a denominator
const portAsk = 'we wrote a highly detailed 50 page spec about how to port the web player trio over to '
  + 'kotlin multiplatform and i told you to give me a 1 to 1 exact replica';

check(verdict({
  prompt: portAsk, finalText: 'The port is complete and matches the spec.', edited: [], searched: true,
}).length === 1, 'completion claimed over an enumerable goal with no count blocks');

check(verdict({
  prompt: portAsk, edited: [], searched: true,
  finalText: 'Ported 34 of 210 surface members; 176 outstanding, listed in the ledger.',
}).length === 0, 'the same claim with the fraction stated stays quiet');

check(verdict({
  prompt: 'fix the typo in the readme', finalText: 'Done.', edited: [], searched: true,
}).length === 0, 'a completion claim over a non-enumerable ask stays quiet');

check(verdict({
  prompt: portAsk, finalText: 'Still working through the surface, nothing to report yet.',
  edited: [], searched: true,
}).length === 0, 'no completion claim, no block');

// Both detectors can fire on one turn, and both belong in the reason.
check(verdict({
  prompt: `${bugReport} and give me the parity across every screen`,
  finalText: 'All done, everything is covered.', edited: ['AudioSync.ts'], searched: false,
}).length === 2, 'a turn can trip both detectors at once');

// ---------------------------------------------------------------- transcript reading, end to end
const dir = join(tmpdir(), `pg-test-${process.pid}`);
mkdirSync(dir, { recursive: true });

function runHook(id, userContent, assistantBlocks, finalText) {
  const tp = join(dir, `${id}.jsonl`);
  writeFileSync(tp, [
    JSON.stringify({ type: 'user', message: { role: 'user', content: userContent } }),
    JSON.stringify({ type: 'assistant', message: { role: 'assistant', content: assistantBlocks } }),
    JSON.stringify({ type: 'assistant', message: { role: 'assistant', content: [{ type: 'text', text: finalText }] } }),
  ].join('\n') + '\n');
  const out = execFileSync('node', [join(import.meta.dirname, 'population-guard.mjs')], {
    input: JSON.stringify({ session_id: `pg-${id}-${Date.now()}`, transcript_path: tp, cwd: process.cwd() }),
    encoding: 'utf8',
  });
  return out.trim().length > 0 && JSON.parse(out).decision === 'block';
}

check(runHook('instance',
  [{ type: 'text', text: bugReport }],
  [{ type: 'tool_use', name: 'Edit', input: { file_path: 'C:/x/AudioSync.ts', old_string: 'a', new_string: 'b' } }],
  'Fixed it.'), 'end to end: edit with no search blocks');

check(!runHook('searched',
  [{ type: 'text', text: bugReport }],
  [
    { type: 'tool_use', name: 'Grep', input: { pattern: 'emitCue' } },
    { type: 'tool_use', name: 'Edit', input: { file_path: 'C:/x/AudioSync.ts', old_string: 'a', new_string: 'b' } },
  ],
  'Fixed all three.'), 'end to end: a Grep in the turn clears detector A');

check(!runHook('shell-search',
  [{ type: 'text', text: bugReport }],
  [
    { type: 'tool_use', name: 'Bash', input: { command: 'rg -n "emitCue" src' } },
    { type: 'tool_use', name: 'Edit', input: { file_path: 'C:/x/AudioSync.ts', old_string: 'a', new_string: 'b' } },
  ],
  'Fixed all three.'), 'end to end: rg in a Bash call counts as the search');

// A plain string user message is the common transcript shape and must parse the same way.
check(runHook('string-content', bugReport,
  [{ type: 'tool_use', name: 'Edit', input: { file_path: 'C:/x/AudioSync.ts', old_string: 'a', new_string: 'b' } }],
  'Fixed.'), 'end to end: string-valued user content is read');

check(userText([{ type: 'user', message: { role: 'user', content: [{ type: 'image' }, { type: 'text', text: 'broken' }] } }])
  .includes('[screenshot]'), 'an attached image is recorded as an anchor');

const tu = toolUse([{
  type: 'assistant',
  message: { role: 'assistant', content: [
    { type: 'tool_use', name: 'Write', input: { file_path: 'x.ts' } },
    { type: 'tool_use', name: 'mcp__aitm__impact', input: {} },
  ] },
}]);
check(tu.edited.length === 1 && tu.searched, 'an aitm impact query counts as a population search');

try { rmSync(dir, { recursive: true, force: true }); } catch { /* best effort */ }
console.log(`\n${pass} passed, ${fail} failed`);
process.exit(fail === 0 ? 0 : 1);
