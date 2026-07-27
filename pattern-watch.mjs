// PostToolUse hook on Bash|PowerShell: notices when the same piece of work keeps being done by hand.
// Repeated manual work is the signal that something should be a script, a skill, an agent, or an MCP
// tool — reliability comes from the thing being written down once, not from redoing it accurately N
// times. The nudge fires exactly once, on the run that crosses the threshold, so it lands when the
// pattern becomes real and never nags afterwards.
//
// Signatures are coarse on purpose: the executable and its subcommand, with paths and arguments
// stripped. "dotnet build <something>" is the recurring shape; the specific file is not.
import { resolveInstance, openWrite } from './brain-lib.mjs';

const THRESHOLD = 5;

// Shell noise that says nothing about what work is being done.
const WRAPPERS = new Set(['cd', 'sudo', 'time', 'env', 'nohup', 'exec', 'command', 'call', '&&', ';', '|']);
const SUBCOMMANDLESS = new Set(['node', 'python', 'python3', 'bash', 'sh', 'pwsh', 'powershell']);

function signature(raw) {
  const first = String(raw).split(/\r?\n/).find((l) => l.trim() && !l.trim().startsWith('#'));
  if (!first) return null;
  // The first stage is usually "cd somewhere"; the intent is in the first stage that actually does
  // something. Stripping only the wrapper WORD left the directory standing in as the executable.
  let words = [];
  for (const stage of first.split(/&&|\|\||;|\|/)) {
    const w = stage.trim().split(/\s+/).filter(Boolean);
    if (w.length === 0) continue;
    if (WRAPPERS.has(w[0]) || w[0].includes('=')) continue;
    words = w;
    break;
  }
  if (words.length === 0) return null;

  const exe = (words[0].split(/[\\/]/).pop() || words[0]).replace(/\.(exe|cmd|ps1)$/i, '').toLowerCase();
  if (!exe || exe.length > 40) return null;

  // For an interpreter the script name IS the subcommand; for a normal CLI it is the next bare word.
  let sub = '';
  if (SUBCOMMANDLESS.has(exe)) {
    const script = words.slice(1).find((w) => !w.startsWith('-'));
    if (script) sub = (script.split(/[\\/]/).pop() || '').toLowerCase();
  } else {
    sub = (words.slice(1).find((w) => !w.startsWith('-') && !w.includes('/') && !w.includes('\\')) || '').toLowerCase();
  }
  if (sub.length > 40) sub = '';
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

    db = await openWrite(resolveInstance(payload));
    if (!db) process.exit(0);
    db.exec(`CREATE TABLE IF NOT EXISTS patterns(
      sig TEXT PRIMARY KEY, sample TEXT, count INTEGER NOT NULL DEFAULT 1,
      first_ts TEXT, last_ts TEXT, promoted INTEGER NOT NULL DEFAULT 0)`);

    const ts = new Date().toISOString();
    db.prepare(
      `INSERT INTO patterns(sig, sample, count, first_ts, last_ts) VALUES(?,?,1,?,?)
       ON CONFLICT(sig) DO UPDATE SET count = count + 1, last_ts = excluded.last_ts`
    ).run(sig, String(cmd).slice(0, 300), ts, ts);

    const row = db.prepare('SELECT count, promoted, sample FROM patterns WHERE sig = ?').get(sig);
    db.close();
    db = null;

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
