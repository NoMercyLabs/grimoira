// Stop-hook guard: closes the write side of the loop. During the turn the gate recorded every search
// the brain could not answer; harvest filled the ones that produced code locations. Whatever is still
// an open gap is something this session learned from the filesystem and never taught back — exactly
// the knowledge that goes missing and has to be re-derived next session.
//
// Evidence-based, not a text heuristic: it only fires on gaps this session actually opened. Blocks at
// most once per session so it can never deadlock, and fails open on any error.
import { resolveInstance, openRead, ledgerPath, readLedger, writeLedger, clip } from './brain-lib.mjs';

let input = '';
process.stdin.on('data', (d) => { input += d; });
process.stdin.on('end', async () => {
  let db = null;
  try {
    const payload = JSON.parse(input || '{}');
    const instance = resolveInstance(payload);
    const lp = ledgerPath(instance, payload.session_id);
    const ledger = readLedger(lp);
    const allowed = ledger.allowed || [];
    if (allowed.length === 0 || ledger.blocked) process.exit(0);

    db = await openRead(instance);
    if (!db) process.exit(0);
    const marks = allowed.map(() => '?').join(',');
    const open = db.prepare(
      `SELECT query, misses FROM gaps WHERE status='open' AND query IN (${marks}) ORDER BY misses DESC LIMIT 8`
    ).all(...allowed);
    db.close();
    db = null;

    if (open.length === 0) process.exit(0);

    ledger.blocked = true;
    writeLedger(lp, ledger);

    const list = open.map((g) => `  • "${clip(g.query, 70)}"${g.misses > 1 ? `  (missed ${g.misses}x)` : ''}`).join('\n');
    process.stdout.write(JSON.stringify({
      decision: 'block',
      reason:
        `This session searched the filesystem for things the aitm brain did not know, and did not teach ` +
        `any of it back. Those lookups are still open gaps:\n\n${list}\n\n` +
        `Record what you actually established for each one before ending the turn — a durable fact via ` +
        `\`aitm add\`, or a rule/decision via the memory channel. State the finding, not the search terms. ` +
        `If a gap turned out to be a dead end or is genuinely not worth storing, say so explicitly in your ` +
        `reply and stop; this guard fires once per session and will not block you again.`,
    }));
  } catch {
    // fail open — never block a normal stop on a guard error
  } finally {
    try { if (db) db.close(); } catch { /* already closed */ }
  }
  process.exit(0);
});
