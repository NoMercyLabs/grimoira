// SessionStart hook: after a compaction / resume / restart, if an aitm count-loop is active
// (done < total), re-inject a directive that tells the model to recover state from AITM and
// continue the in-progress goal to the end. Self-silent when no loop is active. Generic — driven
// entirely by loop-state, tied to no specific task. Pairs with loop-guard.mjs (Stop hook), which
// refuses to end the turn while done < total: that brake stops early-quitting, this one re-arms the
// directive so a freshly-compacted context knows WHAT to continue and to pull it from aitm.
import { readFileSync, existsSync } from 'node:fs';
import { homedir } from 'node:os';
import { join, basename } from 'node:path';

const proj = process.env.CLAUDE_PROJECT_DIR || process.cwd();
const slug = basename(proj.replace(/[\\/]+$/, '')).toLowerCase().replace(/[^a-z0-9_-]/g, '');
const statePath = join(homedir(), '.aitm', slug || 'default', 'loop-state.json');

try {
  if (existsSync(statePath)) {
    const s = JSON.parse(readFileSync(statePath, 'utf8'));
    if (typeof s.done === 'number' && typeof s.total === 'number' && s.done < s.total) {
      const ctx =
        `AUTONOMOUS GOAL ACTIVE — "${s.task}" (${s.done}/${s.total} done). ` +
        `This session is a CONTINUATION, likely after a context compaction or restart. ` +
        `USE AITM FIRST to recover state: call brain_core for the hard rules, then query the aitm ` +
        `project node named in the task (and its milestone slots) to find the ACTIVE milestone; ` +
        `re-read its spec/goal. Resume from the active milestone and drive to the end. The Stop guard ` +
        `blocks ending the turn until ${s.total}/${s.total} — do NOT stop early, and do NOT re-ask the ` +
        `user: everything needed is on the system and in aitm. Run \`aitm loop tick\` only when a ` +
        `milestone is fully verified (compile green + Playwright fidelity green).`;
      process.stdout.write(JSON.stringify({
        hookSpecificOutput: { hookEventName: 'SessionStart', additionalContext: ctx },
      }));
    }
  }
} catch {
  // fail open — never break session start on a guard error
}
process.exit(0);
