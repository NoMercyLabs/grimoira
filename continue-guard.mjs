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
import { tailEntries } from './brain-lib.mjs';

const MAX_BLOCKS = 3;

// The costliest shape, because it looks like diligence: the turn diagnoses a problem it is capable of
// fixing, writes it up as an open item, and hands it back. the owner then has to type the instruction
// that the analysis already implied. Naming a problem you can fix is not reporting, it is deferring.
//
// These phrases only mean deferral when the turn is talking about its OWN work, so they are split.
// Strong ones are already framed as a hand-off and fire alone.
const FLAGGED_STRONG = new RegExp([
  "still open\\b", "still outstanding", "remains? open", "left open",
  "worth a look", "on your side", "you (may|might|could) want to", "someone should",
  "i flagged", "flagging (this|that|it)", "one thing (is |remains )?outstanding",
  "needs? (a )?follow[- ]?up",
].join('|'), 'i');

// Weak ones describe a state, and a state can belong to anyone. "Chapter thumbnails are scaffolded but
// not wired to the player" is a documented product limitation being summarised, not work being parked —
// it blocked a read-only orientation answer that had nothing to continue with.
const FLAGGED_WEAK = new RegExp([
  "still (broken|missing|wrong)", "known (issue|gap|problem|limitation)",
  "not (yet )?(fixed|done|implemented|addressed|wired|handled)",
  "should (also )?be (fixed|done|changed|updated)",
].join('|'), 'i');

// Work described as AVAILABLE rather than done — "the two fixes are ready to start whenever you want
// them". It is a STATEMENT, so hedge-guard never sees it (that one only catches question shapes), and
// it names no problem, so the flagged-item sets never saw it either. It is still parking: specific work
// identified, not started, handed back for the owner to authorise.
const OFFER_READY = /\b(ready (to|for|when|whenever)|queued up|teed up|lined up|on deck|standing by|good to go|waiting (on|for) you|can start|could start|happy to)\b/i;
const YOUR_CALL = /\b(you want|you'?re ready|whenever you|when you'?re|your call|up to you|if you want|say the word)\b/i;
// Idioms that hand work back on their own, with no second marker needed.
const OFFER_ALONE = /\b(say the word|standing where i left it|ready when you are|whenever you want|on your say[- ]so|left it (there|here) for you)\b/i;
// A suggestion handed back with a condition attached: "sensible moment to compact if you want to keep
// going", "good point to X if you'd like". It names an action, does not take it, and makes it his call.
const OFFER_CONDITIONAL = /\b(sensible|good|natural|reasonable|fine) (moment|point|time|place) to\b[^.!?]{0,80}\bif you\b/i;

// What makes a weak phrase a hand-off: the sentence points at the owner or at the turn's own work.
const OWNED = /\b(i|i'?ll|i'?ve|i'?m|we|we'?ll|we'?ve|you|you'?ll|your|my|next step|todo)\b/i;

// Narrating something already dealt with is not parking it. "the thing I flagged before I ran it"
// refers back to analysis in the same reply; it announces no future work and asks for nothing.
const RETROSPECTIVE = /\b(before|earlier|previously|already|above|last (turn|time|session)|which i (then|just)|and (then )?(fixed|did|built))\b/i;

// A phrase MENTIONED is not a phrase USED. Reporting on this guard means quoting the phrases it
// catches — "still open" inside a table cell documenting the patterns fired it, which makes the guard
// unable to be described without tripping. Quoted spans, code spans and table rows carry examples and
// data, never the closing statement that parks work, so they are removed before anything is matched.
function stripMentions(text) {
  return text
    .replace(/```[\s\S]*?```/g, ' ')
    .replace(/`[^`\n]*`/g, ' ')
    .replace(/"[^"\n]{0,120}"/g, ' ')
    .replace(/[“][^”\n]{0,120}[”]/g, ' ')
    .replace(/^\s*\|.*$/gm, ' ');
}

const sentenceAround = (text, hit) => {
  const at = text.toLowerCase().indexOf(hit.toLowerCase());
  if (at < 0) return text;
  const from = Math.max(0, text.lastIndexOf('.', at - 1) + 1);
  const to = text.indexOf('.', at + hit.length);
  return text.slice(from, to < 0 ? text.length : to);
};

function selfFlagged(text) {
  const strong = text.match(FLAGGED_STRONG);
  if (strong && !RETROSPECTIVE.test(sentenceAround(text, strong[0]))) return strong[0];
  const weak = text.match(FLAGGED_WEAK);
  if (weak && OWNED.test(sentenceAround(text, weak[0])) && !RETROSPECTIVE.test(sentenceAround(text, weak[0]))) return weak[0];
  const alone = text.match(OFFER_ALONE);
  if (alone) return alone[0];
  const conditional = text.match(OFFER_CONDITIONAL);
  if (conditional) return conditional[0];
  const ready = text.match(OFFER_READY);
  if (ready && YOUR_CALL.test(sentenceAround(text, ready[0]))) return ready[0];
  return null;
}

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
  // The tail is enough: this only ever looks at the current turn and the latest todo list, and the full
  // read cost 735ms on a 49 MB session — paid on every Stop, alongside three other hooks doing the same.
  const { entries, truncated } = tailEntries(transcriptPath);
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
  return { entries, truncated, turn: entries.slice(start) };
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

    const { entries, truncated, turn } = turnSlice(tp);

    // Latest todo state anywhere in the session; TodoWrite replaces the whole list each call. This is
    // the one thing here that is not turn-local, so when the tail holds no TodoWrite at all the list
    // may simply be older than the window — that case, and only that case, pays for the full read.
    const latestTodos = (from) => {
      let found = null;
      for (const b of blocksOf(from)) {
        if (b.type === 'tool_use' && b.name === 'TodoWrite' && Array.isArray(b.input?.todos)) found = b.input.todos;
      }
      return found;
    };
    let todos = latestTodos(entries);
    if (todos === null && truncated) todos = latestTodos(tailEntries(tp, { maxBytes: Infinity }).entries);
    const pending = (todos || []).filter((t) => t.status === 'pending' || t.status === 'in_progress');

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

    const said = stripMentions(finalText);
    const deferred = DEFERRAL.test(said);
    const parked = pending.length > 0 && !askedThisTurn;
    // A self-flagged item only counts as deferral when nothing actually prevented the fix.
    const hit = REAL_BLOCKER.test(finalText) || askedThisTurn ? null : selfFlagged(said);
    const flagged = hit !== null;
    if (!deferred && !parked && !flagged) process.exit(0);

    const reasons = [];
    if (flagged) {
      reasons.push(
        `You wrote up work you can do instead of doing it (matched: "${hit}"). Diagnosing a problem you are ` +
        `capable of fixing and reporting it as an open item is deferral wearing a status update — it forces ` +
        `the owner to type the instruction your own analysis already implied, which is the single thing he has ` +
        `asked most often to stop. Go fix it now. If something genuinely prevents you, name that specific ` +
        `obstacle instead of listing the symptom.\n\n` +
        `Scope is also a valid answer, and it is NOT a question. If the work is plainly outside what was ` +
        `asked — a refactor surfaced by an orientation question, a rewrite surfaced by a bug report — say ` +
        `so in one flat line and stop. Do not turn it into a menu of options; offering "shall I do A or B" ` +
        `is the hedge-ask, and it costs him the same reply that deferral does.\n\n` +
        `Do NOT satisfy this guard by editing the sentence. Rewriting a doc, a comment, or a report so the ` +
        `phrase stops matching is not a fix, it is a cover-up, and it damages a file nobody asked you to ` +
        `touch. Either change the thing the sentence describes, or say plainly that it is out of scope.`
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
