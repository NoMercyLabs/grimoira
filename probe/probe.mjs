// Dispatch real Claude sessions at grimora and score what they actually do.
//
// Reading the hooks tells you what they should do; only running a session tells you what happens. Each
// scenario is a question with a verifiable answer, phrased the way it would really be asked, and scored
// on the three things that matter: speed, reliability (did the store get asked, or the filesystem), and
// accuracy (is the answer right).
//
// SAFE BY CONSTRUCTION: every session runs read-only, so a probe cannot damage a repo however the agent
// reads the prompt — which is what makes it fair to hand one a genuinely underspecified request.
//
// It is a DENY list, not an allow list. The first run of this harness claimed to be read-only with
// --allowedTools and the sessions used PowerShell and Bash anyway: an allow list does not override a
// user settings file whose permissions.defaultMode is "dontAsk". Only the deny list is a wall.
//
//   node probe/probe.mjs                 run every scenario
//   node probe/probe.mjs encoder-orientation grimora-meaning
import { readFileSync, writeFileSync, mkdirSync, existsSync } from 'node:fs';
import { spawn } from 'node:child_process';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const HERE = dirname(fileURLToPath(import.meta.url));
const CLI = process.env.CLAUDE_CODE_EXECPATH
  || 'C:/Users/dev/.vscode/extensions/anthropic.claude-code-2.1.220-win32-x64/resources/native-binary/claude.exe';

// Anything that can change a file or run a command. The store stays reachable over MCP.
const BLOCKED = ['Bash', 'PowerShell', 'Write', 'Edit', 'MultiEdit', 'NotebookEdit', 'Agent', 'Workflow', 'Artifact'];

const scenarios = JSON.parse(readFileSync(join(HERE, 'scenarios.json'), 'utf8'));
const want = process.argv.slice(2).filter((a) => !a.startsWith('--'));
const chosen = want.length ? scenarios.filter((s) => want.includes(s.id)) : scenarios;

function runOne(scenario) {
  return new Promise((resolve) => {
    const started = Date.now();
    const args = [
      '-p', scenario.prompt,
      '--output-format', 'stream-json',
      '--verbose',
      '--disallowedTools', ...BLOCKED,
      '--permission-mode', 'plan',
    ];
    const proc = spawn(CLI, args, { cwd: 'C:/Projects/NoMercy', shell: false });

    const events = [];
    let buf = '';
    proc.stdout.on('data', (d) => {
      buf += d;
      const lines = buf.split('\n');
      buf = lines.pop();
      for (const l of lines) {
        if (!l.trim()) continue;
        try { events.push(JSON.parse(l)); } catch { /* not a json line */ }
      }
    });
    let stderr = '';
    proc.stderr.on('data', (d) => { stderr += d; });

    const timer = setTimeout(() => proc.kill(), 240_000);
    proc.on('close', () => {
      clearTimeout(timer);
      resolve(score(scenario, events, Date.now() - started, stderr));
    });
  });
}

function score(scenario, events, ms, stderr) {
  const calls = [];
  let answer = '';
  let tokens = 0;

  for (const e of events) {
    const content = e.message?.content;
    if (Array.isArray(content)) {
      for (const b of content) {
        if (b.type === 'tool_use') calls.push({ name: b.name, input: b.input });
        if (b.type === 'text' && b.text.trim()) answer += b.text;
      }
    }
    const u = e.message?.usage;
    if (u) tokens += (u.output_tokens || 0);
    if (e.type === 'result' && typeof e.result === 'string') answer += e.result;
  }

  const asked = calls.some((c) => c.name === 'AskUserQuestion');
  // The store answers in three ways and only one of them is a tool call: the UserPromptSubmit recall
  // injects facts before anything is asked, and a gate denial hands back content in place of the tool.
  // Counting MCP calls alone scored those as "never consulted the store", which is backwards.
  const served = /grimora brain already holds|previous session read this directory|grimora recall \(auto/i;
  const gateServed = events.some((e) => {
    const c = e.message?.content;
    if (!Array.isArray(c)) return false;
    return c.some((b) => b.type === 'tool_result' && served.test(
      typeof b.content === 'string' ? b.content : JSON.stringify(b.content || '')));
  });
  const storeCalls = calls.filter((c) => /grimora|brain|fact|rule|recall|doc/i.test(c.name));
  const reads = calls.filter((c) => c.name === 'Read').map((c) => (c.input?.file_path || '').replace(/\\/g, '/'));
  // Separators are cosmetic. Demanding "packages/nomercy-player-core" failed a run that answered
  // "C:\Projects\NoMercy\packages\nomercy-player-core\" — correct, and richer than what was asked for.
  const lower = answer.toLowerCase().replace(/\\/g, '/');

  const checks = [];
  const add = (ok, label) => checks.push({ ok, label });

  if (scenario.expect === 'ask') {
    // Headless has no interactive UI, so AskUserQuestion is not available and a question in prose is
    // the only way to ask. Requiring the tool marked a session that correctly refused to guess — it
    // replied "which player, and what does broken look like?" — as a failure.
    const askedInProse = /\?/.test(answer) && /\b(which|what|where|do you mean|clarif)/i.test(answer);
    add(asked || askedInProse, 'asked instead of guessing');
    add(!/\b(i (have )?(fixed|changed|updated)|i went ahead)\b/i.test(answer), 'did not act on an unnamed bug');
  } else {
    add(answer.trim().length > 0, 'produced an answer');
    const correct = scenario.mustContain.every((n) => lower.includes(n.toLowerCase()));
    for (const needle of scenario.mustContain) add(lower.includes(needle.toLowerCase()), `says "${needle}"`);
    // The failure is the SWEEP, not the spot-check. A run that took the stored answer and opened one
    // source to confirm it read 1 file where the unfixed store made it read 12 — scoring that as a total
    // failure measured purity rather than cost, and would have argued against verifying anything.
    const allowance = scenario.spotChecks ?? 0;
    for (const banned of scenario.mustNotRead) {
      const hits = reads.filter((r) => r.toLowerCase().includes(banned.toLowerCase())).length;
      add(hits <= allowance, `at most ${allowance} read(s) of ${banned} (did ${hits})`);
    }
    // The store supplying the answer is the goal, and a tool call is only one way it does that: the
    // UserPromptSubmit recall often injects the fact before anything can be asked. Requiring a visible
    // call marked the best possible run — right answer, zero tool calls, two output tokens — as a
    // failure. What matters is that the answer did not come from the filesystem.
    // Same allowance as above: a right answer plus one confirming read is the store doing its job, not
    // the tree. Only a sweep means the store failed to supply it.
    add(storeCalls.length > 0 || gateServed || (correct && reads.length <= allowance),
      'answer came from the store, not the tree');
  }
  add(calls.length <= scenario.maxToolCalls, `<= ${scenario.maxToolCalls} tool calls (used ${calls.length})`);

  return {
    id: scenario.id,
    ms,
    tokens,
    calls: calls.length,
    toolNames: [...new Set(calls.map((c) => c.name))],
    storeCalls: storeCalls.length,
    reads: reads.length,
    asked,
    checks,
    pass: checks.every((c) => c.ok),
    answer: answer.slice(0, 400),
    stderr: stderr.slice(-400),
  };
}

const results = [];
for (const s of chosen) {
  process.stdout.write(`running ${s.id} … `);
  const r = await runOne(s);
  results.push(r);
  console.log(`${r.pass ? 'PASS' : 'FAIL'}  ${Math.round(r.ms / 1000)}s  ${r.calls} calls  ${r.storeCalls} store  ${r.reads} reads`);
  for (const c of r.checks) if (!c.ok) console.log(`      x ${c.label}`);
}

console.log('\n---');
const passed = results.filter((r) => r.pass).length;
console.log(`${passed}/${results.length} passed`);
console.log(`median ${Math.round(results.map((r) => r.ms).sort((a, b) => a - b)[Math.floor(results.length / 2)] / 1000)}s, ` +
  `${results.reduce((n, r) => n + r.calls, 0)} tool calls total, ${results.reduce((n, r) => n + r.reads, 0)} file reads total`);

try {
  mkdirSync(join(HERE, 'runs'), { recursive: true });
  const out = join(HERE, 'runs', `run-${results.length}-scenarios.json`);
  writeFileSync(out, JSON.stringify(results, null, 2));
  console.log(`detail: ${out}`);
} catch { /* reporting only */ }

process.exit(passed === results.length ? 0 : 1);
