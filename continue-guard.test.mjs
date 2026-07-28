// Every case here came from a real transcript: three the guard missed, three it wrongly blocked.
// A phrasing blocklist leaks, so each leak that reaches the owner gets pinned here before it is patched.
import { readFileSync, writeFileSync, mkdirSync, rmSync } from 'node:fs';
import { execFileSync } from 'node:child_process';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

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

const CASES = [
  // Must BLOCK — work named, not done, handed back.
  ['offer-ready', true,
    'Standing where I left it: the two contained Play-listing fixes (deprecated inset APIs, orientation ' +
    'locks) are ready to start whenever you want them; the edge-to-edge opt-out removal wants a device pass.'],
  ['still-open', true, 'Still open: the sprite padding on freshly encoded titles. Worth a look on your side.'],
  ['next-session', true, 'That is a good stopping point — we can pick this up next session.'],
  ['say-the-word', true, 'The migration is written and tested. Say the word and I will run it.'],

  // Must STAY QUIET — none of these park anything.
  ['descriptive', false,
    'Chapter thumbnails are scaffolded but not wired to the player. No 3D stereoscopic. ' +
    'These are documented roadmap items, summarised for orientation.'],
  ['retrospective', false,
    'Fixed. That is the thing I flagged before I ran it, and the gate is green now.'],
  ['plain-done', false, 'Shipped. CI green on 1e6ddc8, 380 pages built, both alerts closed.'],
];

let pass = 0, fail = 0;
for (const [id, want, text] of CASES) {
  const got = fires(text, id);
  const ok = got === want;
  console.log(`  ${ok ? 'ok  ' : 'FAIL'} ${id.padEnd(14)} expected ${want ? 'block' : 'quiet'}, got ${got ? 'block' : 'quiet'}`);
  ok ? pass++ : fail++;
}
try { rmSync(dir, { recursive: true, force: true }); } catch { /* best effort */ }
console.log(`\n${pass} passed, ${fail} failed`);
process.exit(fail === 0 ? 0 : 1);
