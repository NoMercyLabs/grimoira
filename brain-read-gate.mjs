// PreToolUse hook on Read: if this file's directory already has an answer distilled from it, hand the
// answer over instead of the file.
//
// The gap this closes: brain-gate covers Grep and Glob, so a turn that goes straight to Read was never
// gated at all — ls, then twelve Reads, and the store neither answered nor learned. Re-serving the
// stored COPY of a file would be pointless (prose compacts by ~7%); serving the distillation is the win.
//
// Narrow and reversible. It only fires when a synthesis exists for that exact directory, only once per
// directory per session, and never for code — a synthesis cannot substitute for a file you are about to
// edit. Re-issuing the same Read always reaches the disk.
import { readFileSync, existsSync, statSync } from 'node:fs';
import { homedir } from 'node:os';
import { join, dirname, extname, resolve, isAbsolute } from 'node:path';
import { resolveInstance, openRead, ledgerPath, readLedger, writeLedger } from './brain-lib.mjs';

// Prose only. Anything you might Edit has to arrive verbatim, and Edit requires a real prior Read.
const PROSE = new Set(['.md', '.mdx', '.txt', '.rst', '.adoc']);

const allow = () => process.exit(0);

let input = '';
process.stdin.on('data', (d) => { input += d; });
process.stdin.on('end', async () => {
  let db = null;
  try {
    const payload = JSON.parse(input || '{}');
    const raw = payload.tool_input?.file_path || '';
    if (!raw || !PROSE.has(extname(raw).toLowerCase())) allow();
    // An agent working from the repo root writes "docs/x/y.md", and the index is keyed absolutely, so
    // matching the literal string meant the gate never fired for the shape it will mostly be given.
    const file = (isAbsolute(raw) ? raw : resolve(payload.cwd || process.cwd(), raw)).replace(/\\/g, '/');

    const dir = dirname(file);
    const instance = resolveInstance(payload);

    const idx = join(homedir(), '.aitm', instance, 'synthesis.json');
    if (!existsSync(idx)) allow();
    let known = {};
    try { known = JSON.parse(readFileSync(idx, 'utf8')); } catch { allow(); }
    if (!known[dir.toLowerCase()]) allow();

    const lp = ledgerPath(instance, payload.session_id);
    const ledger = readLedger(lp);
    const mark = `synth:${dir.toLowerCase()}`;
    if (ledger.seen[mark]) allow();

    db = await openRead(instance);
    if (!db) allow();
    const row = db.prepare("SELECT title, content FROM docs WHERE k = ? AND category = 'synthesis'")
      .get(`synthesis:${dir.toLowerCase()}`);
    db.close();
    db = null;
    if (!row) allow();

    // A synthesis its sources have outrun is worse than no synthesis, because it reads as current.
    const stamp = Number((row.content.match(/newest_source_ticks: (\d+)/) || [])[1] || 0);
    if (stamp > 0) {
      const names = ((row.content.match(/^from: (.*)$/m) || [])[1] || '').split(',').filter(Boolean);
      for (const n of names) {
        // .NET ticks: 100ns units since year 1, which is the Unix epoch offset below.
        try { if (statSync(join(dir, n.trim())).mtimeMs * 10000 + 621355968000000000 > stamp) allow(); } catch { /* source gone */ }
      }
    }

    ledger.seen[mark] = true;
    writeLedger(lp, ledger);

    process.stdout.write(JSON.stringify({
      hookSpecificOutput: {
        hookEventName: 'PreToolUse',
        permissionDecision: 'deny',
        permissionDecisionReason:
          `A previous session read this directory and the answer it wrote was kept. Read that instead of ` +
          `the files — it is what reading them produces.\n\n--- ${row.title} ---\n\n${row.content}\n\n` +
          `--- end ---\n\nThis is second-hand and says which sources it came from. If you need a detail it ` +
          `does not cover, or you are about to change one of these files, re-issue the identical Read and ` +
          `it will run — this fires once per directory per session.`,
      },
    }));
  } catch {
    // fail open — a guard error must never block a real read
  } finally {
    try { if (db) db.close(); } catch { /* already closed */ }
  }
  process.exit(0);
});
