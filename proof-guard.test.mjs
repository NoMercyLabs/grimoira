// The blocking cases are the real claim that hid the push bug for weeks, plus its nearest relatives.
// The quiet cases matter just as much: an honest partial result must never be punished, or the guard
// teaches the opposite of what it is for.
import { writeFileSync, mkdirSync, rmSync } from 'node:fs';
import { execFileSync } from 'node:child_process';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

const dir = join(tmpdir(), `pg-test-${process.pid}`);
mkdirSync(dir, { recursive: true });

function blocks(finalText, id) {
  const tp = join(dir, `${id}.jsonl`);
  writeFileSync(tp, [
    JSON.stringify({ type: 'user', message: { role: 'user', content: 'check push' } }),
    JSON.stringify({ type: 'assistant', message: { role: 'assistant', content: [{ type: 'text', text: finalText }] } }),
  ].join('\n') + '\n');
  const out = execFileSync('node', [join(import.meta.dirname, 'proof-guard.mjs')], {
    input: JSON.stringify({ session_id: `pg-${id}`, transcript_path: tp, cwd: process.cwd() }),
    encoding: 'utf8',
  });
  return out.trim().length > 0 && JSON.parse(out).decision === 'block';
}

const CASES = [
  // The claim that hid the bug. Component-level evidence, user-level word.
  ['transport-only', true,
    'Push is working. I sent through FcmTransport and the notification was delivered to the token.'],
  ['users-receive', true,
    'Fixed the channel mapping. Users now receive the security alerts.'],
  ['is-live', true, 'The subscription flow is live and confirmed delivery for the test account.'],

  // Honest partial results — the behaviour the guard wants, never punished.
  ['self-limited', false,
    'I sent through FcmTransport and it reached FCM. That proves the transport, not the feature: ' +
    'it bypasses PushGate, so this is not verified end-to-end and still needs a device pass.'],
  ['real-path', false,
    'Delivered. Drove it end-to-end: POST /api/v1/push/subscriptions with a real user token, then ' +
    'observed the notification in the tray on the device.'],
  ['curl-evidence', false,
    'Users receive them now — verified with curl against the real endpoint and a screenshot of the ' +
    'notification shade.'],
  // No outcome claim at all: nothing to police.
  ['no-claim', false,
    'PushGate::isSubscribed falls back to default_enabled when the pivot row is absent. ' +
    'Nothing writes that row; notificationChannels() is never attached or synced anywhere.'],
];

let pass = 0, fail = 0;
for (const [id, want, text] of CASES) {
  const got = blocks(text, id);
  const ok = got === want;
  console.log(`  ${ok ? 'ok  ' : 'FAIL'} ${id.padEnd(14)} expected ${want ? 'block' : 'quiet'}, got ${got ? 'block' : 'quiet'}`);
  ok ? pass++ : fail++;
}
try { rmSync(dir, { recursive: true, force: true }); } catch { /* best effort */ }
console.log(`\n${pass} passed, ${fail} failed`);
process.exit(fail === 0 ? 0 : 1);
