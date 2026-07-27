// PostCompact hook: hand back the anchors the summary paraphrased away.
//
// compact-brief.mjs wrote them before the compaction. Injecting them here is the cheap half of the
// deal — a few hundred tokens of exact paths, branches and open items in place of re-reading files to
// rediscover state the session already established.
import { readFileSync, existsSync, rmSync } from 'node:fs';
import { resolveInstance } from './brain-lib.mjs';
import { briefPath } from './compact-brief.mjs';

let input = '';
process.stdin.on('data', (d) => { input += d; });
process.stdin.on('end', () => {
  try {
    const payload = JSON.parse(input || '{}');
    const instance = resolveInstance(payload);
    const path = briefPath(instance, payload.session_id);
    if (!existsSync(path)) process.exit(0);

    const brief = readFileSync(path, 'utf8');
    // One-shot: a second compaction writes its own brief, and a stale one would describe the wrong turn.
    try { rmSync(path); } catch { /* best effort */ }
    if (brief.trim().length < 40) process.exit(0);

    process.stdout.write(JSON.stringify({
      hookSpecificOutput: {
        hookEventName: 'PostCompact',
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
