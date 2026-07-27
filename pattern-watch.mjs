// PostToolUse hook on Bash|PowerShell: notices when the same piece of work keeps being done by hand.
// Repeated manual work is the signal that something should be a script, a skill, an agent, or an MCP
// tool — reliability comes from the thing being written down once, not from redoing it accurately N
// times. The nudge fires exactly once, on the run that crosses the threshold, so it lands when the
// pattern becomes real and never nags afterwards.
//
// Signatures are coarse on purpose: the executable and its subcommand, with paths and arguments
// stripped. "dotnet build <something>" is the recurring shape; the specific file is not.
import { readFileSync, writeFileSync, mkdirSync } from 'node:fs';
import { homedir } from 'node:os';
import { join, dirname } from 'node:path';
import { resolveInstance, openWrite } from './brain-lib.mjs';

// A single command repeated is weak evidence; the same ORDERED RUN repeated is the actual "way we do
// things", and it is what deserves to become one deterministic step. Sequences therefore trip sooner.
const THRESHOLD = 5;
const SEQ_THRESHOLD = 3;
const SEQ_LEN = 3;

// Shell noise that says nothing about what work is being done.
const WRAPPERS = new Set([
  'cd', 'sudo', 'time', 'env', 'nohup', 'exec', 'command', 'call', 'export', 'set', 'source', '.',
  '&&', ';', '|',
  // PowerShell says "cd" with a different word, and the signature lowercases before this test — without
  // these, every "Set-Location X; <real work>" signed as the navigation instead of the work.
  'set-location', 'sl', 'chdir', 'push-location', 'pushd', 'pop-location', 'popd',
  // Shell keywords open a construct; the work is the command inside it, not the word "for".
  'for', 'while', 'until', 'if', 'do', 'done', 'then', 'else', 'elif', 'fi', 'case', 'esac', 'function',
  'foreach', 'try', 'catch', 'finally', 'begin', 'process', 'end',
]);
const SUBCOMMANDLESS = new Set(['node', 'python', 'python3', 'bash', 'sh', 'pwsh', 'powershell']);
const RUNNERS = new Set(['npm', 'yarn', 'pnpm', 'bun', 'npx', 'dotnet']);
// A bare general-purpose utility is not a task. "grep" repeated fifty times says nothing that could
// be turned into a script; only a named script or subcommand describes work worth codifying.
const UTILITIES = new Set([
  'grep', 'cat', 'sed', 'awk', 'ls', 'cd', 'echo', 'head', 'tail', 'find', 'cut', 'sort', 'uniq',
  'wc', 'tr', 'xargs', 'cp', 'mv', 'rm', 'mkdir', 'touch', 'chmod', 'curl', 'wget', 'jq', 'tee',
  'python', 'python3', 'node', 'bash', 'sh', 'pwsh', 'powershell', 'which', 'test', 'true', 'printf',
]);

function signature(raw) {
  const first = String(raw).split(/\r?\n/).find((l) => l.trim() && !l.trim().startsWith('#'));
  if (!first) return null;
  // The first stage is usually "cd somewhere"; the intent is in the first stage that actually does
  // something. Stripping only the wrapper WORD left the directory standing in as the executable.
  let words = [];
  for (const stage of first.split(/&&|\|\||;|\|/)) {
    // Quotes are shell syntax, not part of the path: without stripping them the script name fails the
    // word test and every quoted invocation collapses to the bare interpreter.
    const w = stage.trim().split(/\s+/).filter(Boolean).map((t) => t.replace(/^["']+|["']+$/g, ''));
    if (w.length === 0) continue;
    if (WRAPPERS.has(w[0]) || w[0].includes('=')) continue;
    // Everything from the first redirect on describes where output went, not what was run.
    const redirect = w.findIndex((t) => /^\d?>>?$|^<$/.test(t) || /^\d?>&\d$/.test(t));
    words = redirect > 0 ? w.slice(0, redirect) : w;
    break;
  }
  if (words.length === 0) return null;

  const exe = (words[0].split(/[\\/]/).pop() || words[0]).replace(/\.(exe|cmd|ps1)$/i, '').toLowerCase();
  if (!exe || exe.length > 40) return null;

  // A redirect is not a subcommand. Without this, "./build.ps1 -Quick 2>&1" signed as "build 2>&1"
  // and the watcher then nagged about a task that is already a script.
  const isWord = (w) => /^[A-Za-z][\w.-]*$/.test(w);

  // For an interpreter the script name IS the subcommand; for a normal CLI it is the next bare word.
  let sub = '';
  if (SUBCOMMANDLESS.has(exe)) {
    const script = words.slice(1).find((w) => !w.startsWith('-') && isWord(w.split(/[\\/]/).pop() || ''));
    if (script) sub = (script.split(/[\\/]/).pop() || '').toLowerCase();
  } else {
    sub = (words.slice(1).find((w) => !w.startsWith('-') && !w.includes('/') && !w.includes('\\') && isWord(w)) || '').toLowerCase();
  }
  // "npm run" names no task; the script after it does. Without this every build, test and lint in the
  // repo collapsed into one signature called "npm run", which can never become anything.
  if (RUNNERS.has(exe) && (sub === 'run' || sub === 'run-script' || sub === 'exec')) {
    const after = words.slice(words.findIndex((w) => w.toLowerCase() === sub) + 1)
      .find((w) => !w.startsWith('-') && isWord(w));
    sub = after ? `${sub} ${after.toLowerCase()}` : sub;
  }
  if (sub.length > 40) sub = '';
  if (!sub && UTILITIES.has(exe)) return null;
  return sub ? `${exe} ${sub}` : exe;
}

let input = '';
process.stdin.on('data', (d) => { input += d; });
process.stdin.on('end', async () => {
  let db = null;
  try {
    const payload = JSON.parse(input || '{}');
    const cmd = payload.tool_input?.command || '';
    const sig = signature(cmd);
    if (!sig) process.exit(0);

    const instance = resolveInstance(payload);
    db = await openWrite(instance);
    if (!db) process.exit(0);
    db.exec(`CREATE TABLE IF NOT EXISTS patterns(
      sig TEXT PRIMARY KEY, sample TEXT, count INTEGER NOT NULL DEFAULT 1,
      first_ts TEXT, last_ts TEXT, promoted INTEGER NOT NULL DEFAULT 0)`);
    db.exec("ALTER TABLE patterns ADD COLUMN kind TEXT NOT NULL DEFAULT 'command'");
  } catch { /* column already present, or store unavailable */ }

  try {
    if (!db) process.exit(0);
    const payload = JSON.parse(input || '{}');
    const cmd = payload.tool_input?.command || '';
    const sig = signature(cmd);
    if (!sig) process.exit(0);
    const instance = resolveInstance(payload);
    const ts = new Date().toISOString();

    const bump = db.prepare(
      `INSERT INTO patterns(sig, sample, count, first_ts, last_ts, kind) VALUES(?,?,1,?,?,?)
       ON CONFLICT(sig) DO UPDATE SET count = count + 1, last_ts = excluded.last_ts`
    );
    bump.run(sig, String(cmd).slice(0, 300), ts, ts, 'command');

    // Rolling window of this session's signatures, so consecutive runs can be seen as one procedure.
    const trailPath = join(homedir(), '.aitm', instance, 'trail', `${String(payload.session_id || 'x').slice(0, 64)}.json`);
    let trail = { sigs: [], cmds: [] };
    try { trail = JSON.parse(readFileSync(trailPath, 'utf8')); } catch { /* first command */ }
    trail.sigs = [...(trail.sigs || []), sig].slice(-SEQ_LEN);
    trail.cmds = [...(trail.cmds || []), String(cmd).slice(0, 200)].slice(-SEQ_LEN);
    try { mkdirSync(dirname(trailPath), { recursive: true }); writeFileSync(trailPath, JSON.stringify(trail)); } catch { /* best effort */ }

    let seqRow = null, seqSig = null;
    if (trail.sigs.length === SEQ_LEN && new Set(trail.sigs).size > 1) {
      seqSig = trail.sigs.join(' -> ');
      bump.run(seqSig, trail.cmds.join('\n'), ts, ts, 'sequence');
      seqRow = db.prepare('SELECT count, promoted, sample FROM patterns WHERE sig = ?').get(seqSig);
    }
    const row = db.prepare('SELECT count, promoted, sample FROM patterns WHERE sig = ?').get(sig);
    db.close();
    db = null;

    // A crossed sequence outranks a crossed single command: it describes the whole procedure.
    if (seqRow && !seqRow.promoted && seqRow.count === SEQ_THRESHOLD) {
      process.stdout.write(JSON.stringify({
        hookSpecificOutput: {
          hookEventName: 'PostToolUse',
          additionalContext:
            `aitm: this exact run has happened ${seqRow.count} times now —\n  ${seqSig}\n\n` +
            `Last time it was:\n${String(seqRow.sample).split('\n').map((l) => `  ${l}`).join('\n')}\n\n` +
            `That is a procedure being re-derived by hand, which is where the steps drift and one gets ` +
            `skipped. Make it one deterministic thing — a checked-in script, a skill, or a subagent — and ` +
            `say what you made. If it should stay ad hoc: ` +
            `UPDATE patterns SET promoted=1 WHERE sig='${seqSig.replace(/'/g, "''")}'.`,
        },
      }));
      process.exit(0);
    }

    if (!row || row.promoted || row.count !== THRESHOLD) process.exit(0);

    process.stdout.write(JSON.stringify({
      hookSpecificOutput: {
        hookEventName: 'PostToolUse',
        additionalContext:
          `aitm: "${sig}" has now been run by hand ${row.count} times in this store.\n` +
          `Last form: ${row.sample}\n\n` +
          `That is a repeated task, and repeated tasks get more reliable by being written down once. ` +
          `Consider whether it should become a checked-in script, a skill, a subagent, or an MCP tool, ` +
          `and say so rather than silently running it a sixth time. If it genuinely should stay ad hoc, ` +
          `mark it settled: UPDATE patterns SET promoted=1 WHERE sig='${sig}'.`,
      },
    }));
  } catch {
    // fail open — never disturb a command on a bookkeeping error
  } finally {
    try { if (db) db.close(); } catch { /* already closed */ }
  }
  process.exit(0);
});
