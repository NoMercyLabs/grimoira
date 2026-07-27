// SessionStart hook: arm the AITM brain for the session. Injects a compact status block — store
// counts, the most-reinforced nodes, open knowledge gaps, and an unflushed-stage warning — plus the
// standing directive to query before guessing and stage learnings aggressively. Reads the per-project
// store directly via node:sqlite (read-only); silent for projects with no store; always fails open.
import { existsSync, statSync } from 'node:fs';
import { homedir } from 'node:os';
import { join, basename } from 'node:path';

try {
  const proj = process.env.CLAUDE_PROJECT_DIR || process.cwd();
  const slug = basename(proj.replace(/[\\/]+$/, '')).toLowerCase().replace(/[^a-z0-9_-]/g, '');
  const root = join(homedir(), '.aitm', slug || 'default');
  const dbPath = join(root, 'aitm.db');
  if (!existsSync(dbPath)) process.exit(0);

  const { DatabaseSync } = await import('node:sqlite');
  const db = new DatabaseSync(dbPath, { readOnly: true });
  // Store is rollback-journal; without this an MCP write window throws SQLITE_BUSY and the
  // fail-open catch silently drops the whole context block (QC-proven: 91% loss under write burst).
  db.exec('PRAGMA busy_timeout = 2000');
  const one = (sql) => { try { return db.prepare(sql).get(); } catch { return undefined; } };
  const all = (sql) => { try { return db.prepare(sql).all(); } catch { return []; } };

  const counts = one(
    "SELECT (SELECT count(*) FROM node_now) AS nodes, (SELECT count(*) FROM triple_now) AS triples, " +
    "(SELECT count(*) FROM facts) AS facts, (SELECT count(*) FROM memory) AS rules"
  );
  if (!counts || counts.nodes === 0) { db.close(); process.exit(0); }

  const top = all(
    "SELECT n.label FROM usage u JOIN node_now n ON n.k = u.node_k ORDER BY u.hits DESC LIMIT 5"
  ).map((r) => r.label);
  const gaps = all(
    "SELECT query, misses FROM gaps WHERE status='open' ORDER BY misses DESC, last_ts DESC LIMIT 3"
  );
  db.close();

  let staged = 0;
  const ledger = join(root, 'pending-learn.jsonl');
  if (existsSync(ledger) && statSync(ledger).size > 0) staged = 1;

  const lines = [
    `AITM BRAIN ACTIVE (${slug}: ${counts.nodes} nodes, ${counts.triples} triples, ${counts.facts} facts, ${counts.rules} rules). ` +
    'Query it BEFORE guessing any project fact/convention (fact, rule, brain_recall, brain_scope, brain_place); ' +
    'stage every new durable fact or correction the moment it appears (brain_stage -> brain_flush); ' +
    'empty lookups auto-log gaps — fill the ones you can.',
  ];
  if (top.length > 0) lines.push(`Hot context: ${top.join('; ')}.`);
  if (gaps.length > 0) lines.push(`Open gaps to fill: ${gaps.map((g) => `"${g.query}" (${g.misses}x)`).join(', ')} — see brain_gaps.`);
  if (staged) lines.push('UNFLUSHED staged learnings from a previous turn — call brain_flush now.');

  process.stdout.write(JSON.stringify({
    hookSpecificOutput: { hookEventName: 'SessionStart', additionalContext: lines.join('\n') },
  }));
} catch {
  // fail open — never break session start on a guard error
}
process.exit(0);
