// UserPromptSubmit hook: hand back the anchors the compaction paraphrased away.
//
// This ran on PostCompact first, which was the wrong door. PostCompact returns only a
// userDisplayMessage — its output goes to the owner's screen and never reaches the model — so the brief
// was being written before every compaction and then shown to the one person who did not need it.
// UserPromptSubmit is the injection point that demonstrably works; prompt-recall uses it.
//
// So the brief waits on disk and lands on the first prompt after the compaction, once. That is a turn
// later than ideal and it is the earliest point the content can actually arrive.
import { readFileSync, existsSync, rmSync } from 'node:fs';
import { resolveInstance, briefPath } from './brain-lib.mjs';

let input = '';
process.stdin.on('data', (d) => { input += d; });
process.stdin.on('end', () => {
  try {
    const payload = JSON.parse(input || '{}');
    const instance = resolveInstance(payload);
    const path = briefPath(instance, payload.session_id);
    if (!existsSync(path)) process.exit(0);

    const brief = readFileSync(path, 'utf8');
    // One-shot: a second compaction writes its own brief, and a stale one describes the wrong turn.
    try { rmSync(path); } catch { /* best effort */ }
    if (brief.trim().length < 40) process.exit(0);

    process.stdout.write(JSON.stringify({
      hookSpecificOutput: {
        hookEventName: 'UserPromptSubmit',
        additionalContext:
          `${brief}\n\nThese are facts recorded at compaction time, not a plan. Continue the work in ` +
          'progress; do not re-derive this state by reading files, and do not restate it back to the owner.',
      },
    }));
  } catch {
    // fail open
  }
  process.exit(0);
});
