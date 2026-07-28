// UserPromptSubmit hook: say how full the context is, at the one moment where acting on it is free.
//
// The product enforces a hard auto-compact ceiling (CLAUDE_AUTOCOMPACT_PCT_OVERRIDE). That ceiling
// fires wherever it lands, which is usually mid-task, and a compaction mid-task is the expensive kind.
// This is the earlier, cheaper signal: at the start of a turn nothing is half-finished, so a compaction
// here costs almost nothing. It nudges once per band crossed, never on every prompt.
import { readFileSync, writeFileSync, mkdirSync, existsSync } from 'node:fs';
import { homedir } from 'node:os';
import { join, dirname } from 'node:path';
import { resolveInstance, tailEntries } from './brain-lib.mjs';

const PREFER = 45;   // guideline: a good moment to compact
const ENFORCE = 70;  // matches CLAUDE_AUTOCOMPACT_PCT_OVERRIDE, where the product compacts on its own

// The transcript records the model WITHOUT its long-context suffix — a 1M session logs plain
// "claude-opus-5" — so the id cannot tell the window apart and reading it did report 106% full.
// The observed peak can: a session that has held more than the small window is on the large one.
const WINDOWS = [200_000, 1_000_000];

function windowFor(peak) {
  const env = Number(process.env.CLAUDE_CODE_MAX_CONTEXT_TOKENS);
  if (env > 0) return env;
  return WINDOWS.find((w) => peak <= w * 0.98) ?? WINDOWS[WINDOWS.length - 1];
}

function used(entries) {
  let latest = 0, peak = 0;
  for (const e of entries) {
    const u = e.message?.usage;
    if (!u) continue;
    const total = (u.input_tokens || 0) + (u.cache_read_input_tokens || 0) + (u.cache_creation_input_tokens || 0);
    if (total <= 0) continue;
    latest = total;
    if (total > peak) peak = total;
  }
  return latest > 0 ? { total: latest, peak } : null;
}

// The peak is what identifies the window, and it can sit anywhere in the session — but re-reading a
// 49 MB transcript on every prompt to find it cost 735ms a turn. Carrying it forward in the state file
// makes the tail sufficient: usage only ever grows within a window, so a running maximum is exact.
const TAIL = 512 * 1024;

let input = '';
process.stdin.on('data', (d) => { input += d; });
process.stdin.on('end', () => {
  try {
    const payload = JSON.parse(input || '{}');
    const tp = payload.transcript_path;
    if (!tp || !existsSync(tp)) process.exit(0);

    const { entries } = tailEntries(tp, { maxBytes: TAIL });
    const u = used(entries);
    if (!u) process.exit(0);

    const instance = resolveInstance(payload);
    const statePath = join(homedir(), '.aitm', instance, 'context-watch.json');
    let state = { sid: null, band: 0, peak: 0 };
    try { state = JSON.parse(readFileSync(statePath, 'utf8')); } catch { /* first run */ }
    if (state.sid !== payload.session_id) state = { sid: payload.session_id, band: 0, peak: 0 };
    state.peak = Math.max(state.peak || 0, u.peak);

    const pct = Math.round((u.total / windowFor(state.peak)) * 100);
    if (pct < PREFER) {
      try { mkdirSync(dirname(statePath), { recursive: true }); writeFileSync(statePath, JSON.stringify(state)); } catch { /* best effort */ }
      process.exit(0);
    }

    const band = pct >= ENFORCE ? 2 : 1;
    if (band <= state.band) process.exit(0);
    state.band = band;
    try { mkdirSync(dirname(statePath), { recursive: true }); writeFileSync(statePath, JSON.stringify(state)); } catch { /* best effort */ }

    // Nothing here may end with "tell the owner". Compaction is not a tool the model has, so raising it
    // hands him a decision he cannot delegate back — the earlier wording ("say so if a compaction now
    // would be sensible") produced exactly that: a turn that stopped to report the context percentage
    // and offer him the choice. The automatic ceiling exists precisely so nobody has to be asked.
    const note = band === 2
      ? `Context is at ${pct}% and the automatic compaction fires on its own at ${ENFORCE}%, so it will ` +
        'land inside whatever you do next. Get ahead of it: write anything durable to the store now with ' +
        '`aitm add`, and finish or checkpoint the step you are on rather than starting a long one. Do ' +
        'not mention any of this to the owner and do not stop — the ceiling handles itself.'
      : `Context is at ${pct}%. Keep durable findings going into the store as you learn them rather than ` +
        'holding them in context, since the store survives a compaction and this conversation does not. ' +
        'Carry on with the work; this is a note to you, not something to report or ask about.';

    process.stdout.write(JSON.stringify({
      hookSpecificOutput: { hookEventName: 'UserPromptSubmit', additionalContext: `aitm context watch: ${note}` },
    }));
  } catch {
    // fail open
  }
  process.exit(0);
});
