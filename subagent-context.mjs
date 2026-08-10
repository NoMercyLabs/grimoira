// SubagentStart hook: arm every spawned agent with the AITM brain. Subagents never see the
// UserPromptSubmit recall, so without this each one starts blind to the ground-truth store.
// Payload carries cwd (instance resolution) but not the agent prompt, so the brief is uniform
// and deliberately lean — it multiplies across every agent in a fan-out. Always fails open.
import { existsSync } from 'node:fs';
import { homedir } from 'node:os';
import { join, basename } from 'node:path';

let input = '';
process.stdin.on('data', (d) => { input += d; });
process.stdin.on('end', async () => {
  try {
    const payload = JSON.parse(input || '{}');
    const proj = process.env.CLAUDE_PROJECT_DIR || payload.cwd || process.cwd();
    const slug = basename(proj.replace(/[\\/]+$/, '')).toLowerCase().replace(/[^a-z0-9_-]/g, '');
    const dbPath = join(homedir(), '.aitm', slug || 'default', 'aitm.db');
    if (!existsSync(dbPath)) process.exit(0);

    const { DatabaseSync } = await import('node:sqlite');
    const db = new DatabaseSync(dbPath, { readOnly: true });
    db.exec('PRAGMA busy_timeout = 2000');
    const counts = db.prepare(
      "SELECT (SELECT count(*) FROM node_now) AS nodes, (SELECT count(*) FROM facts) AS facts, (SELECT count(*) FROM memory) AS rules"
    ).get();
    const top = db.prepare(
      "SELECT n.label FROM usage u JOIN node_now n ON n.k = u.node_k WHERE length(n.label) <= 90 ORDER BY u.hits DESC LIMIT 5"
    ).all().map((r) => r.label);
    db.close();
    if (!counts || counts.nodes === 0) process.exit(0);

    // How the owner wants work done — the operating standard he made after tiring of repeating the same
    // corrections. Injected here so EVERY agent inherits it from one place (most agent .md files don't
    // encode it). Each clause maps to a real, repeated failure; kept terse because it multiplies across
    // a fan-out.
    const standard =
      'How the owner wants the work done: finish the FULL scope — count the members (files, platforms, variants, call-sites) and do every one, state N of M; an unstated fraction reads as done and is the costliest lie. ' +
      'Fix the root cause and its siblings, never only the one example given. ' +
      'Verify by RUNNING it and reading real output, not by reading the source. ' +
      'Report terse: lead with the result, no hedge/offer tails, never defer work you can do. ' +
      'Only lint/format the files YOU changed — never a whole-project lint:fix/format when the tree has unrelated dirty WIP. ' +
      'Never reset/commit/checkout git state unless the task explicitly says to. Never write AI/meta files outside .claude/. ' +
      'If the change touches client UI or behavior, it is a CROSS-CLIENT job: web (nomercy-app-web) and mobile+TV (nomercy-app-kmp) are first-party siblings that must match — find the same component/behavior in the OTHER clients before changing, fix at the shared level (player trio / NM component contract / design token) not one copy, mirror or justify per client, and verify no sibling regressed (state N of M clients checked). A one-client fix that diverges the others is the whack-a-mole the owner is tired of. ' +
      'For a Moooom/NM design-system component the Mom source .css is the ONLY oracle — NEVER invent values: run `python scripts/mom-parity.py <name>` for Mom\'s exact spec before writing it and `python scripts/mom-parity.py <name> <client-file>` to verify zero drift after. Render the identical Mom appearance on every client, but handle each platform\'s interaction: web hover + :focus-visible, mobile press, TV D-pad focus.';
    const ctx =
      `AITM ground-truth store active (${counts.nodes} nodes, ${counts.facts} facts, ${counts.rules} rules). ` +
      'Before stating any project fact (URL, path, port, field, convention) query the aitm MCP tools — fact(), rule(), brain_recall(), brain_place() — instead of guessing; they refuse rather than hallucinate. ' +
      'If you learn a durable fact or hit a wrong/empty answer, report it to your caller so it gets staged. ' +
      standard +
      (top.length > 0 ? ` Hot context: ${top.join('; ')}.` : '');
    process.stdout.write(JSON.stringify({
      hookSpecificOutput: { hookEventName: 'SubagentStart', additionalContext: ctx },
    }));
  } catch {
    // fail open — never break agent spawn on a context error
  }
  process.exit(0);
});
