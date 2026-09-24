// Integration check for the three code-graph MCP tools (graph_query / graph_path / graph_explain)
// against a real spawned server — the C# selftest ("aitm selftest --instance test") covers the same
// three fixture scenarios in-process and faster; this file exists to catch a wiring defect the
// in-process test cannot: a tool name typo, a missing [McpServerTool], or a JSON-RPC argument mismatch
// that only shows up once the assembly is actually loaded and called over stdio.
import { spawn } from 'node:child_process';
import { existsSync, mkdirSync, rmSync } from 'node:fs';
import { homedir } from 'node:os';
import { join } from 'node:path';
import { openWrite } from './brain-lib.mjs';

const INSTANCE = 'graphtest';
const STORE_DIR = join(homedir(), '.aitm', INSTANCE);

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
    setTimeout(() => { p.kill(); resolve(out); }, 8000);
  });
}

function textOf(out, id) {
  for (const line of out.split('\n')) {
    try {
      const msg = JSON.parse(line);
      if (msg.id === id && msg.result?.content) return msg.result.content[0].text;
    } catch { /* not JSON, or not this line */ }
  }
  return '';
}

// Fresh instance so this never collides with a real workspace's graph. mcp.cs's Open() creates the
// rest of the schema (usage/gaps/meta) on first connect, but `edges` only exists once the CLI's own
// Init() has run at least once against this instance — so seed via the CLI-compatible openWrite path.
try { rmSync(STORE_DIR, { recursive: true, force: true }); } catch { /* fine, didn't exist */ }
mkdirSync(STORE_DIR, { recursive: true });

const db = await (async () => {
  // openWrite refuses a store that doesn't exist yet, so touch the CLI once to create the schema.
  const { spawnSync } = await import('node:child_process');
  spawnSync('dotnet', [join(import.meta.dirname, 'bin-cli', 'aitm.dll'), 'init', '--instance', INSTANCE], { stdio: 'ignore' });
  return openWrite(INSTANCE);
})();

const insert = db.prepare('INSERT INTO edges(symbol,contract,project,file,line,usage,hardcoded) VALUES(?,?,?,?,?,?,0)');
insert.run('McpFixtureWidget', 'decl', 'alpha', 'alpha/widget.ts', 10, 'ts declaration');
insert.run('McpFixtureWidget', '', 'beta', 'beta/x.ts', 5, 'uses widget');
insert.run('McpFixtureWidget', '', 'gamma', 'gamma/y.ts', 9, 'uses widget too');
insert.run('McpPathFixtureA', 'decl', 'alpha', 'pathfixture/shared.ts', 1, 'ts declaration');
insert.run('McpPathFixtureB', 'decl', 'alpha', 'pathfixture/shared.ts', 2, 'ts declaration');
db.close();

const out = await rpc([
  { name: 'graph_query', args: { question: 'McpFixtureWidget' } },
  { name: 'graph_path', args: { a: 'McpPathFixtureA', b: 'McpPathFixtureB' } },
  { name: 'graph_path', args: { a: 'NoSuchSymbolEver', b: 'McpFixtureWidget' } },
  { name: 'graph_explain', args: { symbol: 'McpFixtureWidget' } },
]);

const q = textOf(out, 1);
const pOk = textOf(out, 2);
const pMiss = textOf(out, 3);
const e = textOf(out, 4);

try { rmSync(STORE_DIR, { recursive: true, force: true }); } catch { /* best effort cleanup */ }

const checks = [
  ['graph_query: finds the fixture symbol', q.includes('McpFixtureWidget')],
  ['graph_query: groups it under its home project', q.includes('[alpha]')],
  ['graph_path: finds the 2-hop path', pOk.includes('(2 hop(s))')],
  ['graph_path: unresolvable symbol refuses cleanly', pMiss.includes('no symbol or file matches')],
  ['graph_explain: shows the declaration site', e.includes('alpha/widget.ts:10')],
  ['graph_explain: lists users grouped by project', e.includes('beta') && e.includes('gamma')],
];

let fail = 0;
for (const [what, ok] of checks) {
  console.log(`  ${ok ? 'ok  ' : 'FAIL'} ${what}`);
  if (!ok) fail++;
}
if (fail) console.log(`\nq=${q.slice(0, 200)}\npOk=${pOk.slice(0, 200)}\npMiss=${pMiss.slice(0, 200)}\ne=${e.slice(0, 200)}`);
console.log(`\n${checks.length - fail} passed, ${fail} failed`);
process.exit(fail === 0 ? 0 : 1);
