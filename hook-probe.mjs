// Run a hook against a synthetic payload and print what it decided, in readable form.
//
//   node hook-probe.mjs brain-history.mjs '{"tool_name":"Read","tool_input":{"file_path":"..."}}'
//   node hook-probe.mjs continue-guard.mjs @payload.json
//
// Hooks answer in JSON on stdout and say nothing at all when they pass a call through, so testing one
// by hand means writing the same decoder inline every time — which on Windows means fighting shell
// quoting for a throwaway script. This is that decoder, once.
import { readFileSync } from 'node:fs';
import { spawnSync } from 'node:child_process';
import { dirname, join, isAbsolute } from 'node:path';
import { fileURLToPath } from 'node:url';

const HERE = dirname(fileURLToPath(import.meta.url));
const [hookArg, payloadArg] = process.argv.slice(2);

if (!hookArg || !payloadArg) {
  console.error('usage: node hook-probe.mjs <hook.mjs> \'<json>\' | @file.json');
  process.exit(1);
}

const hook = isAbsolute(hookArg) ? hookArg : join(HERE, hookArg);
const payload = payloadArg.startsWith('@') ? readFileSync(payloadArg.slice(1), 'utf8') : payloadArg;

const run = spawnSync(process.execPath, [hook], { input: payload, encoding: 'utf8' });
const out = (run.stdout || '').trim();

if (run.stderr && run.stderr.trim()) console.error(`stderr: ${run.stderr.trim()}`);
if (!out) {
  console.log('PASS-THROUGH — the hook said nothing, so the tool call proceeds unchanged.');
  process.exit(0);
}

let parsed;
try { parsed = JSON.parse(out); } catch { console.log(out); process.exit(0); }

const h = parsed.hookSpecificOutput || {};
if (parsed.decision === 'block') {
  console.log('BLOCK\n');
  console.log(parsed.reason);
} else if (h.permissionDecision === 'deny') {
  console.log('DENY\n');
  console.log(h.permissionDecisionReason);
} else if (h.updatedInput) {
  console.log('REWRITE');
  if (h.permissionDecisionReason) console.log(`  ${h.permissionDecisionReason}`);
  console.log(`\n${JSON.stringify(h.updatedInput, null, 2)}`);
} else if (h.additionalContext) {
  console.log('CONTEXT INJECTED\n');
  console.log(h.additionalContext);
} else if (parsed.systemMessage) {
  console.log(`MESSAGE: ${parsed.systemMessage}`);
} else {
  console.log(JSON.stringify(parsed, null, 2));
}
