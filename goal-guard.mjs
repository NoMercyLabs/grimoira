// Stop-hook guard: catches the scope-narrowing trap — declaring the OBJECTIVE done while listing
// leftovers bucketed as "intentionally left / out of scope / for now / remains". The literal task may be
// finished but the user's actual GOAL (clean up X, remove all Y, finish Z) isn't, and the remainder is
// rationalized rather than done. Blocks so each leftover is re-tested against the goal. Generic — tied to
// no task. Fails OPEN. A genuine, per-item-justified hold (named reason) is NOT bucketing and won't trip.
import { readFileSync } from 'node:fs';

function lastAssistantText(transcriptPath) {
  const lines = readFileSync(transcriptPath, 'utf8').split('\n').filter((l) => l.trim().length > 0);
  for (let i = lines.length - 1; i >= 0; i--) {
    let o;
    try { o = JSON.parse(lines[i]); } catch { continue; }
    const isAssistant = o.type === 'assistant' || o.message?.role === 'assistant';
    if (!isAssistant) continue;
    const content = o.message?.content;
    if (!Array.isArray(content)) continue;
    const text = content.filter((b) => b.type === 'text').map((b) => b.text).join('').trim();
    if (text.length > 0) return text;
  }
  return '';
}

function isScopeNarrowing(text) {
  const t = text.toLowerCase();
  // Strong, GLOBAL completion claim (not a mid-task "step X done") — keeps progress updates from tripping.
  const claimedDone = /\b(clean slate|all (done|clean|cleaned)|fully (done|clean|cleaned)|— done|task (done|complete|finished)|objective (met|complete|achieved)|nothing (left|lost|remains)|everything('s| is) (done|clean))\b/.test(t);
  // Leftover bucketed as deliberate/deferred rather than done — the rationalization vocabulary.
  const bucketed = /\b(intentionally left|left dirty|left as[- ]?is|left untouched|out of scope|follow[- ]?up|for now|didn'?t (get to|touch)|leftover|still (remains|dirty|there|stale)|won'?t touch|skipp(ed|ing)|remain(s|ing) (as )?(bloat|dirty|stale|uncommitted))\b/.test(t);
  return claimedDone && bucketed;
}

let input = '';
process.stdin.on('data', (d) => { input += d; });
process.stdin.on('end', () => {
  try {
    const payload = JSON.parse(input || '{}');
    const tp = payload.transcript_path;
    if (tp) {
      const text = lastAssistantText(tp);
      if (text && isScopeNarrowing(text)) {
        process.stdout.write(JSON.stringify({
          decision: 'block',
          reason:
            'You declared the OBJECTIVE done while also listing items left behind ("intentionally left / out of ' +
            'scope / for now / remains"). Re-test each leftover against the STATED GOAL, not the slice you scoped: if ' +
            'the goal was to clean up / remove / finish something and any of it remains only because it was bucketed ' +
            'as intentional/out-of-scope/later, it is NOT done — finish it now. Deferral is valid ONLY when an item ' +
            'genuinely conflicts with the goal or a HARD rule (e.g. a dangerous outward-facing push); then name THAT ' +
            'specific reason per item instead of bucketing. Do NOT stop on rationalized partial completion.',
        }));
      }
    }
  } catch {
    // fail open — never block a normal stop on a guard error
  }
  process.exit(0);
});
