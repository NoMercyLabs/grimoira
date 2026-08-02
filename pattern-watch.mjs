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
// Stages that contain no command: the navigation itself, and the header of a loop or conditional.
const NAVIGATION = new Set(['cd', 'chdir', 'set-location', 'sl', 'pushd', 'popd', 'push-location', 'pop-location']);
const HEADERS = new Set(['for', 'foreach', 'while', 'until', 'if', 'elif', 'case', 'switch', 'select']);
// Keywords that sit in FRONT of a real command in the same stage.
const BODY_KEYWORDS = new Set([
  'do', 'then', 'else', 'done', 'fi', 'esac', 'end', 'try', 'catch', 'finally', 'begin', 'process',
  'sudo', 'time', 'env', 'nohup', 'exec', 'command', 'call', 'export', 'set', 'source', '.', 'function',
  '&&', ';', '|',
]);

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
const SCRIPT_FILE = /\.(mjs|cjs|js|ts|py|ps1|sh|bat|cmd|rb|pl)$/i;
const PRIMITIVES = new Set([
  'git add', 'git rm', 'git mv', 'git status', 'git log', 'git diff', 'git show', 'git branch',
  'git checkout', 'git switch', 'git stash', 'git fetch', 'git rev-parse', 'git ls-files',
  'gh api', 'gh auth',
]);
// A bare general-purpose utility is not a task. "grep" repeated fifty times says nothing that could
// be turned into a script; only a named script or subcommand describes work worth codifying.
const UTILITIES = new Set([
  'grep', 'cat', 'sed', 'awk', 'ls', 'cd', 'echo', 'head', 'tail', 'find', 'cut', 'sort', 'uniq',
  'wc', 'tr', 'xargs', 'cp', 'mv', 'rm', 'mkdir', 'touch', 'chmod', 'curl', 'wget', 'jq', 'tee',
  'python', 'python3', 'node', 'bash', 'sh', 'pwsh', 'powershell', 'which', 'test', 'true', 'printf',
  // Waiting is not work. "sleep" had accumulated 269 hits and could never become anything.
  'sleep', 'start-sleep', 'timeout', 'wait', 'date', 'pwd', 'basename', 'dirname', 'seq', 'read',
]);

// Exported for pattern-watch.test.mjs. Seven bugs have lived in here — cd swallowing the real command,
// a redirect read as a subcommand, quotes collapsing a script to its interpreter, the PowerShell
// spelling of cd, shell keywords, "npm run" naming no task, and codifying things already codified —
// and every one reached the owner as a bogus nudge before it was found.
export function signature(raw) {
  const first = String(raw).split(/\r?\n/).find((l) => l.trim() && !l.trim().startsWith('#'));
  if (!first) return null;
  // The first stage is usually "cd somewhere"; the intent is in the first stage that actually does
  // something. Stripping only the wrapper WORD left the directory standing in as the executable.
  let words = [];
  for (const stage of first.split(/&&|\|\||;|\|/)) {
    // Quotes are shell syntax, not part of the path: without stripping them the script name fails the
    // word test and every quoted invocation collapses to the bare interpreter.
    let w = stage.trim().split(/\s+/).filter(Boolean).map((t) => t.replace(/^["']+|["']+$/g, ''));
    if (w.length === 0) continue;
    // Two kinds of wrapper, and treating them alike broke both ways. A NAVIGATION or LOOP-HEADER stage
    // has no command in it at all — stripping just the keyword left "cd C:/Projects/aitm" signing as
    // "aitm", the directory standing in for the executable, and "for q in a b" signing as "q in".
    // A BODY keyword shares its stage with the real command ("do ./aitm.exe doc"), so skipping that
    // stage threw the command away and a whole loop signed as nothing.
    const head = w[0].toLowerCase();
    if (NAVIGATION.has(head) || HEADERS.has(head)) continue;
    // An assignment is not a command, in either shell spelling. `r=$(curl …)` left "-s" standing as the
    // executable, and PowerShell's spaced form `$p = Get-CimInstance …` signed as "$p get-ciminstance",
    // because only the glued form contained an "=" to notice.
    while (w.length > 0 && (
      BODY_KEYWORDS.has(w[0].toLowerCase())
      || w[0].includes('=')
      || w[0] === '='
      || (w.length > 1 && w[1] === '=' && /^\$?[\w.]+$/.test(w[0]))
    )) w = w.slice(1);
    // What survives has to look like a command. After an assignment is stripped the remainder often
    // starts with a flag or a substitution, and neither names any task worth codifying.
    if (w.length === 0 || w[0].startsWith('-') || w[0].startsWith('$')) continue;
    // Everything from the first redirect on describes where output went, not what was run.
    const redirect = w.findIndex((t) => /^\d?>>?$|^<$/.test(t) || /^\d?>&\d$/.test(t));
    words = redirect > 0 ? w.slice(0, redirect) : w;
    break;
  }
  if (words.length === 0) return null;

  const exeToken = words[0].split(/[\\/]/).pop() || words[0];
  // Script-ness is decided on the ORIGINAL token, before the extension is stripped: "./build.ps1"
  // becomes "build", and by then nothing distinguishes it from a bare command. Invoking a checked-in
  // script is already the deterministic form, so proposing to codify it is circular.
  if (SCRIPT_FILE.test(exeToken)) return null;
  const exe = exeToken.replace(/\.(exe|cmd)$/i, '').toLowerCase();
  if (!exe || exe.length > 40) return null;
  // A bare general-purpose utility is not a task whatever follows it — "grep foo" is a search for foo,
  // not a procedure. Checking this only when there was no subcommand let every one of them through.
  if (UTILITIES.has(exe) && !RUNNERS.has(exe) && !SUBCOMMANDLESS.has(exe)) return null;

  // A redirect is not a subcommand. Without this, "./build.ps1 -Quick 2>&1" signed as "build 2>&1"
  // and the watcher then nagged about a task that is already a script. The colon is allowed because
  // npm script names use it — "test:unit" failed the word test and collapsed back to "yarn run".
  const isWord = (w) => /^[A-Za-z][\w.:-]*$/.test(w);

  // For an interpreter the script name IS the subcommand; for a normal CLI it is the next bare word.
  let sub = '';
  if (SUBCOMMANDLESS.has(exe)) {
    const script = words.slice(1).find((w) => !w.startsWith('-') && isWord(w.split(/[\\/]/).pop() || ''));
    if (script) sub = (script.split(/[\\/]/).pop() || '').toLowerCase();
  } else {
    // A token straight after a flag is that flag's VALUE, not the subcommand. "adb -s emulator-5560
    // shell input" signed as "adb emulator-5560" — the device serial — so every device and every
    // session produced its own signature and none of them named the work.
    const rest = words.slice(1);
    sub = (rest.find((w, i) => !w.startsWith('-') && !(i > 0 && rest[i - 1].startsWith('-'))
      && !w.includes('/') && !w.includes('\\') && isWord(w)) || '').toLowerCase();
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
  const sig = sub ? `${exe} ${sub}` : exe;
  // A version-control primitive is not a task. "git rm run 5 times" describes nothing that could become
  // a script — these only carry meaning as steps INSIDE a procedure, and the sequence detector already
  // sees them there. It is how "git add -> aitm add -> gh run" was correctly flagged.
  if (PRIMITIVES.has(sig)) return null;
  // Running a checked-in script is the codified form already. Asking to codify continue-guard.test.mjs,
  // which is a test file wired into verify.ps1, is circular — the answer to "make this deterministic"
  // is the very file being run. Sequences containing scripts still count: that is where ship.ps1 came
  // from, a procedure built out of steps that were each already scripts.
  if (SCRIPT_FILE.test(sub)) return null;
  return sig;
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
