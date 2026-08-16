// PreToolUse hook on Grep|Glob: put the search pattern to the brain and attach what it knows, once per
// signature per session, so a search of a million-line monorepo arrives with the context an earlier
// session already paid for. A miss is recorded as a gap so the store learns what it does not know.
//
// The search itself is never blocked. Recall is background; the tree is ground truth. Fails open.
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
    // The search RUNS. It used to be denied with these hits offered in its place, and that cost real
    // work: FTS matches tokens, not meaning, so a search for one symbol came back with five unrelated
    // rows, and "act on it and skip the search" invited the agent to treat them as the result. One
    // session edited tests on the strength of an intercepted search and had to undo it.
    //
    // A Grep asks what the tree contains RIGHT NOW. Recall cannot answer that at any confidence: its
    // absence proves nothing and its presence may describe code that has since changed. So the hits
    // ride ALONGSIDE the real results as background, never instead of them.
    process.stdout.write(JSON.stringify({
      hookSpecificOutput: {
        hookEventName: 'PreToolUse',
        additionalContext:
          `aitm recall — BACKGROUND ONLY, not the result of this search. It may be stale, and FTS ` +
          `matches tokens rather than meaning, so some of it is probably unrelated. The ` +
          `${payload.tool_name || 'search'} below is the ground truth; never act on these rows in ` +
          `place of it, and never conclude from them that something is absent.\n\n${format(picks)}\n\n` +
          `If the search finds something the brain lacked, record it (aitm add / index-memory).`,
      },
    }));
  } catch {
    // fail open — a guard error must never block a real tool call
  } finally {
    try { if (db) db.close(); } catch { /* already closed */ }
  }
  process.exit(0);
});
