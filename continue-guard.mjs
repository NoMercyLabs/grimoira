// Stop-hook guard: the turn ends when the work runs out or when an answer is genuinely needed, and
// not before. Two failures this exists to kill, both of which cost the owner a round trip every time:
//
//   1. Deferral — "next session", "it's late", "we've done enough for today", "picking this up later".
//      There is no next session from his side; there is him typing "continue".
//   2. Silent parking — ending with pending todos and no question asked, which reads as done but isn't.
//
// The only legitimate stop with work outstanding is one where the next step genuinely depends on his
// answer, and the canonical way to ask is the AskUserQuestion tool. Prose questions do not count:
// they are how the hedge-ask tail sneaks back in.
//
// Never deadlocks: after MAX_BLOCKS consecutive blocks it stands down, so a guard misfire cannot trap
// the session. Fails open on any error.
import { readFileSync, existsSync, writeFileSync, mkdirSync } from 'node:fs';
import { homedir } from 'node:os';
import { join, basename, dirname } from 'node:path';

const MAX_BLOCKS = 3;

// The costliest shape, because it looks like diligence: the turn diagnoses a problem it is capable of
// fixing, writes it up as an open item, and hands it back. the owner then has to type the instruction
// that the analysis already implied. Naming a problem you can fix is not reporting, it is deferring.
const SELF_FLAGGED = new RegExp([
  "still open\\b", "still outstanding", "remains? open", "left open", "still (broken|missing|wrong)",
  "not (yet )?(fixed|done|implemented|addressed|wired|handled)", "known (issue|gap|problem|limitation)",
  "worth a look", "on your side", "you (may|might|could) want to", "someone should",
  "i flagged", "flagging (this|that|it)", "one thing (is |remains )?outstanding",
  "needs? (a )?follow[- ]?up", "should (also )?be (fixed|done|changed|updated)",
].join('|'), 'i');

// A genuine capability limit is not deferral: there is nothing to continue with.
const REAL_BLOCKER = new RegExp([
  "no browser tooling", "cannot access", "can'?t access", "no credentials", "not authorised", "not authorized",
  "requires (your|a) (answer|decision|approval|login)", "only you can", "need(s)? your (call|answer|decision)",
  "waiting on (ci|the build|a deploy)", "rate limit", "blocked by",
].join('|'), 'i');

const DEFERRAL = new RegExp([
  "next session", "another session", "future session", "follow[- ]?up session",
  "it'?s (getting )?late", "call it a (day|night)", "enough for (today|now|one day)",
  "we'?ve done enough", "that'?s enough for", "good (place to )?stop", "natural stopping point",
  "pick(ing)? (this|it) up (later|tomorrow)", "resume (this )?(later|tomorrow|next)",
  "leave (it|this) (here|for now)", "come back to (this|it) (later|tomorrow)",
  "tomorrow(\\.|,| we| i'?ll)", "in a later (turn|session|pass)",
].join('|'), 'i');

function turnSlice(transcriptPath) {
  const lines = readFileSync(transcriptPath, 'utf8').split('\n').filter((l) => l.trim().length > 0);
  const entries = [];
  for (const l of lines) { try { entries.push(JSON.parse(l)); } catch { /* skip */ } }
  // Everything since the last genuine user prompt is "this turn".
  let start = 0;
  for (let i = entries.length - 1; i >= 0; i--) {
    const e = entries[i];
    const isUser = e.type === 'user' || e.message?.role === 'user';
    if (!isUser) continue;
    const c = e.message?.content;
    const isToolResult = Array.isArray(c) && c.some((b) => b.type === 'tool_result');
    if (isToolResult) continue;
    start = i;
    break;
  }
  return { entries, turn: entries.slice(start) };
}

const blocksOf = (entries) => {
  const out = [];
  for (const e of entries) {
    const c = e.message?.content;
    if (Array.isArray(c)) out.push(...c);
  }
  return out;
};

let input = '';
process.stdin.on('data', (d) => { input += d; });
process.stdin.on('end', () => {
  try {
    const payload = JSON.parse(input || '{}');
    const tp = payload.transcript_path;
    if (!tp || !existsSync(tp)) process.exit(0);

    const proj = process.env.CLAUDE_PROJECT_DIR || payload.cwd || process.cwd();
    const slug = basename(proj.replace(/[\\/]+$/, '')).toLowerCase().replace(/[^a-z0-9_-]/g, '');
    const statePath = join(homedir(), '.aitm', slug || 'default', 'continue-guard.json');
    let state = { sid: null, blocks: 0 };
    try { state = JSON.parse(readFileSync(statePath, 'utf8')); } catch { /* first run */ }
    if (state.sid !== payload.session_id) state = { sid: payload.session_id, blocks: 0 };
    if (state.blocks >= MAX_BLOCKS) process.exit(0);

    const { entries, turn } = turnSlice(tp);

    // Latest todo state anywhere in the session; TodoWrite replaces the whole list each call.
    let pending = [];
    for (const b of blocksOf(entries)) {
      if (b.type === 'tool_use' && b.name === 'TodoWrite' && Array.isArray(b.input?.todos)) {
        pending = b.input.todos.filter((t) => t.status === 'pending' || t.status === 'in_progress');
      }
    }

    const askedThisTurn = blocksOf(turn).some((b) => b.type === 'tool_use' && b.name === 'AskUserQuestion');

    let finalText = '';
    for (let i = entries.length - 1; i >= 0; i--) {
      const e = entries[i];
      if (!(e.type === 'assistant' || e.message?.role === 'assistant')) continue;
      const c = e.message?.content;
      if (!Array.isArray(c)) continue;
      const t = c.filter((b) => b.type === 'text').map((b) => b.text).join('').trim();
      if (t.length > 0) { finalText = t; break; }
    }

    const deferred = DEFERRAL.test(finalText);
    const parked = pending.length > 0 && !askedThisTurn;
    // A self-flagged item only counts as deferral when nothing actually prevented the fix.
    const flagged = SELF_FLAGGED.test(finalText) && !REAL_BLOCKER.test(finalText) && !askedThisTurn;
    if (!deferred && !parked && !flagged) process.exit(0);

    const reasons = [];
    if (flagged) {
      const hit = (finalText.match(SELF_FLAGGED) || [''])[0];
      reasons.push(
        `You wrote up work you can do instead of doing it (matched: "${hit}"). Diagnosing a problem you are ` +
        `capable of fixing and reporting it as an open item is deferral wearing a status update — it forces ` +
        `the owner to type the instruction your own analysis already implied, which is the single thing he has ` +
        `asked most often to stop. Go fix it now. If something genuinely prevents you, name that specific ` +
        `obstacle instead of listing the symptom.`
      );
    }
    if (deferred) {
      reasons.push(
        'You ended on DEFERRAL language ("next session" / "late" / "enough for today" / "pick this up later"). ' +
        'There is no next session from the owner\'s side — there is only him typing "continue", which is the exact ' +
        'friction this guard exists to remove. Delete that framing and do the next item now.'
      );
    }
    if (parked) {
      const list = pending.slice(0, 6).map((t) => `  • ${t.content}`).join('\n');
      reasons.push(
        `${pending.length} todo(s) are still open and you did not ask a blocking question:\n${list}\n` +
        'A todo list is authorization to finish every item, not a status board. Continue with the next one.'
      );
    }
    reasons.push(
      'Stop ONLY when the next step genuinely depends on an answer only the owner can give — and then ask it with ' +
      'the AskUserQuestion tool, which is what this guard recognises. A question in prose does not count; that is ' +
      'the hedge-ask tail. If everything really is done, say what shipped, flatly, with no offers and no epilogue.'
    );

    state.blocks += 1;
    try { mkdirSync(dirname(statePath), { recursive: true }); writeFileSync(statePath, JSON.stringify(state)); } catch { /* best effort */ }

    process.stdout.write(JSON.stringify({ decision: 'block', reason: reasons.join('\n\n') }));
  } catch {
    // fail open — never trap a session on a guard error
  }
  process.exit(0);
});
