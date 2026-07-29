// Cases are drawn from DIFFERENT stacks on purpose. A guard written against one incident passes a test
// written against that same incident and protects nothing else — which is exactly the failure it
// exists to prevent, committed one level up. Every blocking case below is the same class of mistake
// wearing a different subsystem's clothes.
import { writeFileSync, mkdirSync, rmSync } from 'node:fs';
import { execFileSync } from 'node:child_process';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

const dir = join(tmpdir(), `pg-test-${process.pid}`);
mkdirSync(dir, { recursive: true });

function blocks(finalText, id) {
  const tp = join(dir, `${id}.jsonl`);
  writeFileSync(tp, [
    JSON.stringify({ type: 'user', message: { role: 'user', content: 'is it done' } }),
    JSON.stringify({ type: 'assistant', message: { role: 'assistant', content: [{ type: 'text', text: finalText }] } }),
  ].join('\n') + '\n');
  const out = execFileSync('node', [join(import.meta.dirname, 'proof-guard.mjs')], {
    input: JSON.stringify({ session_id: `pg-${id}`, transcript_path: tp, cwd: process.cwd() }),
    encoding: 'utf8',
  });
  return out.trim().length > 0 && JSON.parse(out).decision === 'block';
}

const CASES = [
  // --- MUST BLOCK: one class, six stacks ---
  ['php-internal-call', true,
    'Push is working. I invoked the transport directly and the notification was delivered.'],
  ['kmp-compile', true,
    'The seek regression is fixed. compileDebugKotlin builds clean with no type errors.'],
  ['dotnet-unit', true,
    'The encoder crop fix works now. The unit tests pass against hand-built profile records.'],
  ['npm-workspace', true,
    'The plugin API is working for consumers. Verified locally against the copied dist in the testbed.'],
  ['ci-local', true,
    'That is fixed. The suite is green locally so the pipeline will be fine.'],
  ['docs-count', true,
    'The docs site is functional again. File count is back up and there are no warnings in the output.'],

  // --- MUST STAY QUIET: evidence at the claim's altitude, on varied surfaces ---
  ['real-request', false,
    'Delivered. POST /api/v1/push/subscriptions with a real user token, then observed it in the tray on the device.'],
  ['real-device', false,
    'The seek regression is fixed — reproduced on the device, then re-ran and watched it in logcat.'],
  ['real-media', false,
    'The crop fix works. Re-ran the encode job against a real media file and checked the output.'],
  ['published-artifact', false,
    'Working for consumers: installed the published package in a fresh clone and imported it.'],
  ['ci-watched', false,
    'That is fixed. Pushed and watched the run — CI is green.'],

  // --- MUST STAY QUIET: honest partial results are the behaviour being taught ---
  ['self-limited', false,
    'It compiles clean, but that proves the component, not the feature — I have not driven the real ' +
    'path and it still needs a device pass.'],
  ['names-the-gap', false,
    'The unit tests pass. That would not catch a gate above this layer denying the request, so treat ' +
    'this as a proxy until it runs end to end.'],

  // --- MUST STAY QUIET: no claim that anything works ---
  ['analysis-only', false,
    'The gate falls back to the channel default when the pivot row is absent, and nothing anywhere ' +
    'writes that row.'],
];

let pass = 0, fail = 0;
for (const [id, want, text] of CASES) {
  const got = blocks(text, id);
  const ok = got === want;
  console.log(`  ${ok ? 'ok  ' : 'FAIL'} ${id.padEnd(19)} expected ${want ? 'block' : 'quiet'}, got ${got ? 'block' : 'quiet'}`);
  ok ? pass++ : fail++;
}
try { rmSync(dir, { recursive: true, force: true }); } catch { /* best effort */ }
console.log(`\n${pass} passed, ${fail} failed`);
process.exit(fail === 0 ? 0 : 1);
