// PostToolUse hook on Read: the track record for the file just opened. Before a piece of code is
// changed, the reasoning that produced it has to be on the table — the rules it obeys, the decisions
// that shaped it, the conversation that argued it. Otherwise a fix lands on top of a constraint
// nobody remembers and breaks something that was working on purpose.
//
// This is the write-side counterpart to brain-gate: the gate stops a blind search, this stops a blind
// edit. Injected as context, never a block, and fired once per file per session so a file read
// repeatedly in one turn does not repeat itself. Fails open.
import { resolveInstance, openRead, historyFor, format, ledgerPath, readLedger, writeLedger } from './brain-lib.mjs';

// Meta, build, and lockfile noise has no decision history worth surfacing.
const EXEMPT = /(node_modules|[\\/]dist[\\/]|[\\/]build[\\/]|[\\/]bin[\\/]|[\\/]obj[\\/]|\.lock$|\.min\.|\.map$|\.svg$|\.png$|\.jpg$|\.ico$|package-lock|yarn\.lock)/i;

let input = '';
process.stdin.on('data', (d) => { input += d; });
process.stdin.on('end', async () => {
  let db = null;
  try {
    const payload = JSON.parse(input || '{}');
    const file = payload.tool_input?.file_path || '';
    if (!file || EXEMPT.test(file)) process.exit(0);

    const instance = resolveInstance(payload);
    const lp = ledgerPath(instance, payload.session_id);
    const ledger = readLedger(lp);
    ledger.files = ledger.files || {};
    if (ledger.files[file]) process.exit(0);

    db = await openRead(instance);
    if (!db) process.exit(0);
    const picks = historyFor(db, file, { limit: 4 });
    db.close();
    db = null;

    ledger.files[file] = true;
    writeLedger(lp, ledger);
    if (picks.length === 0) process.exit(0);

    process.stdout.write(JSON.stringify({
      suppressOutput: true,
      hookSpecificOutput: {
        hookEventName: 'PostToolUse',
        additionalContext:
          `aitm track record for ${file.replace(/\\/g, '/').split('/').slice(-2).join('/')} — why this code is the ` +
          `way it is. Weigh this before changing it; if a decision here still holds, work with it rather than ` +
          `over it, and if you deliberately overturn one, say so and record the new decision.\n\n${format(picks)}`,
      },
    }));
  } catch {
    // fail open — never disturb a Read
  } finally {
    try { if (db) db.close(); } catch { /* already closed */ }
  }
  process.exit(0);
});
