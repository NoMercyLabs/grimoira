// PreToolUse hook on Grep|Glob: no blind searches of a million-line monorepo while the store already
// holds the answer. The search pattern is put to the brain first; if the brain answers, the tool call
// is denied and the hits are handed back in its place. If the brain misses, the search runs and the
// miss is recorded as a gap so the store learns what it does not know.
//
// It never deadlocks: a signature is gated at most once per session, so re-issuing the same search
// always reaches the filesystem. Fails open on any error.
import {
  resolveInstance, openRead, openWrite, tokenize, signature, search, format, logGap,
  ledgerPath, readLedger, writeLedger,
} from './brain-lib.mjs';

// Meta/scratch trees are workspace plumbing, not project knowledge worth gating. Session transcripts
// are already in the chat channel, so grepping the raw jsonl is a filesystem question, not a recall one.
const EXEMPT = /(^|[\\/])(\.claude[\\/](worktrees|projects|plugins)|scratchpad|node_modules|\.git|dist|build|bin|obj)([\\/]|$)/i;

// Searching one named file is not a blind search — the file is already identified, and the store has
// nothing to add about where to look. Only a scope that is a directory tree is worth gating.
const NAMED_FILE = /\.[A-Za-z0-9]{1,6}$/;

const allow = () => process.exit(0);

let input = '';
process.stdin.on('data', (d) => { input += d; });
process.stdin.on('end', async () => {
  let db = null;
  try {
    const payload = JSON.parse(input || '{}');
    // Glob asks which files exist, which is a question about the filesystem and not one any store can
    // answer. Gating it returned topically-adjacent prose in place of a directory listing, cost a
    // re-issue, and on this monorepo the re-issued glob then hit ripgrep's timeout. Harvest still runs
    // on Glob results — learning the paths is useful; refusing the listing never was.
    if (payload.tool_name === 'Glob') allow();
    const ti = payload.tool_input || {};
    const pattern = ti.pattern || '';
    const scope = ti.path || ti.glob || '';
    if (!pattern || EXEMPT.test(String(scope))) allow();
    if (ti.path && NAMED_FILE.test(String(ti.path))) allow();

    const tokens = tokenize(pattern);
    if (tokens.length === 0) allow();

    const instance = resolveInstance(payload);
    const sig = signature(tokens);
    const lp = ledgerPath(instance, payload.session_id);
    const ledger = readLedger(lp);
    if (ledger.seen[sig]) allow();

    db = await openRead(instance);
    if (!db) allow();
    const picks = search(db, tokens, { limit: 5 });
    db.close();
    db = null;

    ledger.seen[sig] = true;

    if (picks.length === 0) {
      // The brain genuinely does not know this. Let the search run, and record what it could not
      // answer — brain-capture reads these at Stop and brain-harvest fills them from the results.
      ledger.allowed = [...new Set([...(ledger.allowed || []), sig])].slice(-64);
      writeLedger(lp, ledger);
      const w = await openWrite(instance);
      if (w) { logGap(w, payload.tool_name === 'Glob' ? 'glob' : 'grep', sig); w.close(); }
      allow();
    }

    writeLedger(lp, ledger);
    process.stdout.write(JSON.stringify({
      hookSpecificOutput: {
        hookEventName: 'PreToolUse',
        permissionDecision: 'deny',
        permissionDecisionReason:
          `The aitm brain already holds knowledge matching this search — read it before scanning the tree.\n\n` +
          `${format(picks)}\n\n` +
          `If that answers the question, act on it and skip the search. If it genuinely does not, re-issue ` +
          `the identical ${payload.tool_name || 'search'} call and it will run — this gate fires once per ` +
          `search per session. When the search then finds something the brain lacked, record it ` +
          `(aitm add / index-memory) so the next session does not pay for it again.`,
      },
    }));
  } catch {
    // fail open — a guard error must never block a real tool call
  } finally {
    try { if (db) db.close(); } catch { /* already closed */ }
  }
  process.exit(0);
});
