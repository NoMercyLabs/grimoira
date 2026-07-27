// Stop-hook guard: catches the hedge-ask tail the owner has flagged repeatedly — ending a turn with
// "want me to…? / should I…? / let me know if it's off / is this the direction". Reads the final
// assistant message from the transcript and blocks the stop so the ending gets rewritten: either do
// the obvious next thing, or end flat on the result. A genuinely required decision goes through the
// AskUserQuestion tool, not trailing prose. Fails OPEN — any error never blocks a normal stop.
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

function isHedgeTail(text) {
  // Only inspect the tail — the violation is always at the very end, never mid-explanation.
  const tail = text.slice(-400).toLowerCase();
  const offerThenQuestion = /\b(want me to|should i|shall i|do you want me to|would you like me to)\b[^.?!]*\?/;
  const qcHedge = /\b(let me know if|tell me if|if it'?s off|if that'?s off|is this the (right )?direction|eyeball it|does this look right|or want me to)\b/;
  return offerThenQuestion.test(tail) || qcHedge.test(tail);
}

let input = '';
process.stdin.on('data', (d) => { input += d; });
process.stdin.on('end', () => {
  try {
    const payload = JSON.parse(input || '{}');
    const tp = payload.transcript_path;
    if (tp) {
      const text = lastAssistantText(tp);
      if (text && isHedgeTail(text)) {
        process.stdout.write(JSON.stringify({
          decision: 'block',
          reason:
            'Your turn ends on a HEDGE-ASK TAIL (e.g. "want me to…?" / "let me know if it\'s off" / "is this the ' +
            'direction") — a HARD rule the owner has flagged repeatedly; it triggers "Bruh". Rewrite the ending: either DO ' +
            'the obvious next step now, or end flat on the RESULT. If a user decision is genuinely required, ask it via ' +
            'the AskUserQuestion tool, not trailing prose — then stop. Do NOT re-stop with the tail still there.',
        }));
      }
    }
  } catch {
    // fail open — never block a normal stop on a guard error
  }
  process.exit(0);
});
