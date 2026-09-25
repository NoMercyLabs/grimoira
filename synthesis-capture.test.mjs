// Drives the REAL hook against REAL directories on disk, because the bug it exists to prevent was
// invisible to synthetic payloads: a synthesis was filed over 3 of 412 independent memory notes and
// then intercepted every read of every memory file with an answer about something unrelated.
//
// The discriminator is coverage. 12 of 12 report parts is a document read whole; 3 of 412 notes is a
// sample of unrelated things. Nothing else separates them, and file count alone cannot.
//
// The directories are real in shape only: built fresh under the OS temp dir for this run, never the
// live NoMercy report tree or the real memory folder, and never the live "nomercy" store. A test
// instance name always contains "test" or "fixture" (test-store-guard.mjs enforces it) so this suite
// can never resolve onto a populated store by accident.
import { writeFileSync, mkdirSync, rmSync } from 'node:fs';
import { execFileSync } from 'node:child_process';
import { homedir } from 'node:os';
import { join } from 'node:path';
import { assertTestInstance } from './test-store-guard.mjs';

const INSTANCE = `synth-test-${process.pid}`;
assertTestInstance(INSTANCE);

// Not under the OS temp dir: the hook itself excludes any path with a "Temp"/"tmp" segment as scratch
// (SCRATCH in synthesis-capture.mjs), which a real report or memory folder never has. A fixture placed
// there would silently exercise the exclusion instead of the coverage logic under test.
const base = join(import.meta.dirname, '.test-fixtures', INSTANCE);
const dir = join(base, 'work'); // scratch for the fake transcripts, separate from the fixture trees
const REPORT = join(base, 'report');
const MEMORY = join(base, 'memory');
for (const p of [dir, REPORT, MEMORY]) mkdirSync(p, { recursive: true });

// A report read whole: enough parts that a quarter of it is still a real sample, not the whole set.
const REPORT_FILES = 8;
for (let i = 0; i < REPORT_FILES; i++) {
  writeFileSync(join(REPORT, `0${i}-part.md`), `# part ${i}\ncontent for part ${i}\n`);
}
// A shelf of many independent notes: reading 3 of these is a sample, never a sweep.
const MEMORY_FILES = 60;
for (let i = 0; i < MEMORY_FILES; i++) {
  writeFileSync(join(MEMORY, `note-${i}.md`), `# note ${i}\nunrelated topic ${i}\n`);
}

const prose = (d, n) => Array.from({ length: n }, (_, i) => d === REPORT ? join(REPORT, `0${i}-part.md`) : join(MEMORY, `note-${i}.md`));

// A long answer, since a short reply is correctly never stored.
const ANSWER = 'x '.repeat(900);

// Forces every path AITM resolves an instance from (env, and the payload it falls back to) to this
// disposable one, so nothing here can land in a real project's store regardless of what the shell
// that runs the suite happens to have set.
const testEnv = { ...process.env, CLAUDE_PROJECT_DIR: base };

function fires(files, id) {
  const tp = join(dir, `${id}.jsonl`);
  const content = [{ type: 'text', text: ANSWER }];
  for (const f of files) content.push({ type: 'tool_use', name: 'Read', input: { file_path: f } });
  writeFileSync(tp, [
    JSON.stringify({ type: 'user', message: { role: 'user', content: 'summarise these' } }),
    JSON.stringify({ type: 'assistant', message: { role: 'assistant', content } }),
  ].join('\n') + '\n');
  const out = execFileSync('node', [join(import.meta.dirname, 'synthesis-capture.mjs')], {
    input: JSON.stringify({ session_id: `sc-${id}`, transcript_path: tp, cwd: base }),
    encoding: 'utf8',
    env: testEnv,
  });
  return out.includes('kept your answer');
}

const CASES = [
  ['whole-report', true, prose(REPORT, REPORT_FILES), 'a document read whole is a set'],
  ['third-of-report', false, prose(REPORT, Math.max(3, Math.floor(REPORT_FILES / 4))),
    'a quarter of a report is a sample, not a sweep'],
  ['few-of-many', false, prose(MEMORY, 3),
    'three of hundreds of independent notes must never become a synthesis'],
];

let pass = 0, fail = 0;
for (const [id, want, files, why] of CASES) {
  // Each case must start clean, or a synthesis stored by a previous case masks the result.
  try { execFileSync(join(import.meta.dirname, 'bin-cli', 'aitm.exe'),
    ['shed-synthesis', '--path', files[0].replace(/\/[^/]+$/, ''), '--instance', INSTANCE], { encoding: 'utf8' }); } catch { /* none stored */ }
  const got = fires(files, id);
  const ok = got === want;
  console.log(`  ${ok ? 'ok  ' : 'FAIL'} ${id.padEnd(16)} ${files.length} file(s) — ${why}`);
  ok ? pass++ : fail++;
  if (got) {
    try { execFileSync(join(import.meta.dirname, 'bin-cli', 'aitm.exe'),
      ['shed-synthesis', '--path', files[0].replace(/\/[^/]+$/, ''), '--instance', INSTANCE], { encoding: 'utf8' }); } catch { /* fine */ }
  }
}

// The guard itself: a name with no "test" or "fixture" in it must never be accepted.
{
  let threw = false;
  try { assertTestInstance('nomercy'); } catch { threw = true; }
  const ok = threw === true;
  console.log(`  ${ok ? 'ok  ' : 'FAIL'} guard-refuses-real   a real-looking instance name is rejected before any write`);
  ok ? pass++ : fail++;
}

try { rmSync(base, { recursive: true, force: true }); } catch { /* best effort */ }
try { rmSync(join(homedir(), '.aitm', INSTANCE), { recursive: true, force: true }); } catch { /* not always present */ }
console.log(`\n${pass} passed, ${fail} failed`);
process.exit(fail === 0 ? 0 : 1);
