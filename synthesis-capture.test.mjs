// Drives the REAL hook against REAL directories on disk, because the bug it exists to prevent was
// invisible to synthetic payloads: a synthesis was filed over 3 of 412 independent memory notes and
// then intercepted every read of every memory file with an answer about something unrelated.
//
// The discriminator is coverage. 12 of 12 report parts is a document read whole; 3 of 412 notes is a
// sample of unrelated things. Nothing else separates them, and file count alone cannot.
import { writeFileSync, mkdirSync, rmSync, readdirSync, existsSync } from 'node:fs';
import { execFileSync } from 'node:child_process';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

const dir = join(tmpdir(), `sc-test-${process.pid}`);
mkdirSync(dir, { recursive: true });

const REPORT = 'C:/Projects/NoMercy/docs/shipping-in-the-dark/reports/the-effortless-encoder';
const MEMORY = 'C:/Users/dev/.claude/projects/c--Projects-NoMercy/memory';

const prose = (d) => {
  try { return readdirSync(d).filter((f) => /\.(md|mdx|txt)$/i.test(f)).map((f) => `${d}/${f}`); } catch { return []; }
};

// A long answer, since a short reply is correctly never stored.
const ANSWER = 'x '.repeat(900);

function fires(files, id) {
  const tp = join(dir, `${id}.jsonl`);
  const content = [{ type: 'text', text: ANSWER }];
  for (const f of files) content.push({ type: 'tool_use', name: 'Read', input: { file_path: f } });
  writeFileSync(tp, [
    JSON.stringify({ type: 'user', message: { role: 'user', content: 'summarise these' } }),
    JSON.stringify({ type: 'assistant', message: { role: 'assistant', content } }),
  ].join('\n') + '\n');
  const out = execFileSync('node', [join(import.meta.dirname, 'synthesis-capture.mjs')], {
    input: JSON.stringify({ session_id: `sc-${id}`, transcript_path: tp, cwd: 'C:/Projects/NoMercy' }),
    encoding: 'utf8',
  });
  return out.includes('kept your answer');
}

const reportFiles = prose(REPORT);
const memoryFiles = prose(MEMORY);
const CASES = [];

if (reportFiles.length >= 6) {
  CASES.push(['whole-report', true, reportFiles, 'a document read whole is a set']);
  CASES.push(['third-of-report', false, reportFiles.slice(0, Math.max(3, Math.floor(reportFiles.length / 4))),
    'a quarter of a report is a sample, not a sweep']);
} else {
  console.log('  skip  report fixture missing — cannot exercise the positive case');
}

if (memoryFiles.length >= 50) {
  CASES.push(['few-of-many', false, memoryFiles.slice(0, 3),
    'three of hundreds of independent notes must never become a synthesis']);
} else {
  console.log('  skip  memory fixture too small — cannot exercise the shelf case');
}

let pass = 0, fail = 0;
for (const [id, want, files, why] of CASES) {
  // Each case must start clean, or a synthesis stored by a previous case masks the result.
  try { execFileSync(join(import.meta.dirname, 'bin-cli', 'aitm.exe'),
    ['shed-synthesis', '--path', files[0].replace(/\/[^/]+$/, ''), '--instance', 'nomercy'], { encoding: 'utf8' }); } catch { /* none stored */ }
  const got = fires(files, id);
  const ok = got === want;
  console.log(`  ${ok ? 'ok  ' : 'FAIL'} ${id.padEnd(16)} ${files.length} file(s) — ${why}`);
  ok ? pass++ : fail++;
  if (got) {
    try { execFileSync(join(import.meta.dirname, 'bin-cli', 'aitm.exe'),
      ['shed-synthesis', '--path', files[0].replace(/\/[^/]+$/, ''), '--instance', 'nomercy'], { encoding: 'utf8' }); } catch { /* fine */ }
  }
}

try { rmSync(dir, { recursive: true, force: true }); } catch { /* best effort */ }
console.log(`\n${pass} passed, ${fail} failed`);
process.exit(fail === 0 ? 0 : 1);
