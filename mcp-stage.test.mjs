// brain_stage rejected kind="rule" on every call for weeks. The first argument names the ROW type,
// and for a node row the node's OWN kind goes in the next slot — two things called kind, one parameter
// named for both. A perfect error message that fires every time is an API defect, not a user defect.
//
// Row types and node kinds are disjoint, so "rule" could only ever have meant a node of kind rule.
// This drives the built server over stdio, because the coercion is only real if a row actually lands.
import { spawn } from 'node:child_process';
import { readFileSync, existsSync, rmSync } from 'node:fs';
import { homedir } from 'node:os';
import { join } from 'node:path';

// A dedicated instance, so this never touches real pending work and never depends on which directory
// the server happens to resolve its instance from — the first run wrote to C:/Users/dev/.aitm/aitm/ because the
// spawned process took its instance from its own cwd, and the reply still said it had staged.
const INSTANCE = 'stagetest';
const LEDGER = join(homedir(), '.aitm', INSTANCE, 'pending-learn.jsonl');

function rpc(calls) {
  return new Promise((resolve) => {
    const p = spawn('dotnet', [join(import.meta.dirname, 'bin', 'mcp.dll')], { stdio: ['pipe', 'pipe', 'pipe'], env: { ...process.env, AITM_INSTANCE: INSTANCE } });
    let out = '';
    p.stdout.on('data', (d) => { out += d; });
    p.stdin.write(JSON.stringify({
      jsonrpc: '2.0', id: 0, method: 'initialize',
      params: { protocolVersion: '2024-11-05', capabilities: {}, clientInfo: { name: 't', version: '1' } },
    }) + '\n');
    p.stdin.write(JSON.stringify({ jsonrpc: '2.0', method: 'notifications/initialized' }) + '\n');
    calls.forEach((c, i) => p.stdin.write(JSON.stringify({
      jsonrpc: '2.0', id: i + 1, method: 'tools/call', params: { name: c.name, arguments: c.args },
    }) + '\n'));
    setTimeout(() => { p.kill(); resolve(out); }, 9000);
  });
}

try { if (existsSync(LEDGER)) rmSync(LEDGER); } catch { /* fine */ }

const KEY = 'stage-coercion-probe';
const out = await rpc([
  { name: 'brain_stage', args: { kind: 'rule', key: KEY, a: 'a probe label', b: 'the full statement' } },
]);

const ledger = existsSync(LEDGER) ? readFileSync(LEDGER, 'utf8') : '';
const row = ledger.split('\n').filter(Boolean).map((l) => { try { return JSON.parse(l); } catch { return null; } })
  .filter(Boolean).find((r) => r.key === KEY);

try { if (existsSync(LEDGER)) rmSync(LEDGER); } catch { /* fine */ }

const checks = [
  ['was not rejected', !/rejected:/.test(out)],
  ['a row was actually staged', !!row],
  ['stored as a node row', row?.k === 'node'],
  ['node kind is "rule"', row?.kind === 'rule'],
  ['label kept', row?.label === 'a probe label'],
  ['statement kept', row?.gloss === 'the full statement'],
  ['the reply explains the coercion', /ROW type/.test(out)],
];

let fail = 0;
for (const [what, ok] of checks) {
  console.log(`  ${ok ? 'ok  ' : 'FAIL'} ${what}`);
  if (!ok) fail++;
}
if (fail) console.log(`\nserver said: ${out.slice(-400)}`);
console.log(`\n${checks.length - fail} passed, ${fail} failed`);
process.exit(fail === 0 ? 0 : 1);
