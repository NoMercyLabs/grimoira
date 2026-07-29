// Stop hook: a claim about what the USER gets needs evidence from where the user enters.
//
// The failure this exists to prevent, in full: push notifications were reported as "delivered" for
// weeks. Every proof called FcmTransport directly. PushGate sits UPSTREAM of that transport and was
// denying every push for every user, so the proof was structurally incapable of observing the defect —
// its blind spot was exactly the shape of the bug, and green was guaranteed before it ran.
//
// The rule that generalises: a test entered BELOW the layer that is broken cannot fail. So the question
// is never "did it pass", it is "where did this proof enter, and what could it therefore never see".
//
// A memory already said "test the real path, not a mock of it" and it was violated anyway, because the
// cheap instrument is always the one that produces a green signal fastest. Hence a hook.
import { readFileSync, existsSync } from 'node:fs';
import { homedir } from 'node:os';
import { join, basename, dirname } from 'node:path';
import { mkdirSync, writeFileSync } from 'node:fs';
import { tailEntries } from './brain-lib.mjs';

const MAX_BLOCKS = 2;

// Words that assert a USER-FACING outcome. These are the claims that need end-to-end evidence.
const OUTCOME_CLAIM = new RegExp([
  "\\b(deliver(s|ed|ing)?|arriv(e|es|ed|ing))\\b",
  "\\b(users?|clients?|devices?|the (app|phone|tv|browser)) (now )?(get|gets|receive|receives|see|sees|can)\\b",
  "\\bworks? (now|end[- ]to[- ]end|in production|for (users|everyone))\\b",
  "\\b(it|this|the feature|the flow) (is|are) (live|working|functional)\\b",
  "\\bverified working\\b",
  "\\bconfirmed (delivery|receipt)\\b",
].join('|'), 'i');

// Evidence that the proof entered where a user enters, or observed where a user observes.
const ENTRY_EVIDENCE = new RegExp([
  "\\bend[- ]to[- ]end\\b", "\\bthrough the (real|full|whole) (path|flow|stack)\\b",
  "\\b(POST|GET|PUT|PATCH|DELETE) /", "\\bcurl\\b", "\\bvia the (api|endpoint|ui|dashboard|app)\\b",
  "\\bon (the )?(device|phone|tv|emulator|handset)\\b", "\\bscreenshot\\b", "\\blogcat\\b",
  "\\breal (user|account|token|session)\\b", "\\bfrom the (browser|app|client)\\b",
  "\\bnotification (tray|shade|centre|center)\\b", "\\bobserved (on|in)\\b",
].join('|'), 'i');

// Saying the proof was partial is not the failure — that is the honest report this guard wants.
const SELF_LIMITED = new RegExp([
  "\\bnot (yet )?(verified|tested|proven) end[- ]to[- ]end\\b",
  "\\bproves? the (component|transport|unit|function), not\\b",
  "\\bhave not (driven|exercised|run) the real (path|flow)\\b",
  "\\bbypass(es|ed|ing) the\\b", "\\bskipp(ed|ing) the (gate|middleware|auth)\\b",
  "\\bstill needs? a (device|real) (pass|run|check)\\b",
].join('|'), 'i');

const stripCode = (t) => t.replace(/```[\s\S]*?```/g, ' ').replace(/`[^`\n]*`/g, ' ');

let input = '';
process.stdin.on('data', (d) => { input += d; });
process.stdin.on('end', () => {
  try {
    const payload = JSON.parse(input || '{}');
    const tp = payload.transcript_path;
    if (!tp || !existsSync(tp)) process.exit(0);

    const proj = process.env.CLAUDE_PROJECT_DIR || payload.cwd || process.cwd();
    const slug = basename(proj.replace(/[\\/]+$/, '')).toLowerCase().replace(/[^a-z0-9_-]/g, '');
    const statePath = join(homedir(), '.aitm', slug || 'default', 'proof-guard.json');
    let state = { sid: null, blocks: 0 };
    try { state = JSON.parse(readFileSync(statePath, 'utf8')); } catch { /* first run */ }
    if (state.sid !== payload.session_id) state = { sid: payload.session_id, blocks: 0 };
    if (state.blocks >= MAX_BLOCKS) process.exit(0);

    const { entries } = tailEntries(tp);
    let finalText = '';
    for (let i = entries.length - 1; i >= 0; i--) {
      const e = entries[i];
      if (!(e.type === 'assistant' || e.message?.role === 'assistant')) continue;
      const c = e.message?.content;
      if (!Array.isArray(c)) continue;
      const t = c.filter((b) => b.type === 'text').map((b) => b.text).join('').trim();
      if (t.length > 0) { finalText = t; break; }
    }
    if (!finalText) process.exit(0);

    const said = stripCode(finalText);
    const claim = said.match(OUTCOME_CLAIM);
    if (!claim) process.exit(0);
    if (ENTRY_EVIDENCE.test(said) || SELF_LIMITED.test(said)) process.exit(0);

    state.blocks += 1;
    try { mkdirSync(dirname(statePath), { recursive: true }); writeFileSync(statePath, JSON.stringify(state)); } catch { /* best effort */ }

    process.stdout.write(JSON.stringify({
      decision: 'block',
      reason:
        `You claimed a user-facing outcome (matched: "${claim[0]}") without naming where the proof ` +
        `entered the system.\n\n` +
        `A test entered BELOW the broken layer cannot fail. Push notifications were reported delivered ` +
        `for weeks because every proof called the transport directly while the gate above it denied ` +
        `every push — the check was structurally incapable of seeing the defect, and green was ` +
        `guaranteed before it ran.\n\n` +
        `Answer these three before you end the turn:\n` +
        `  1. WHERE DID THE PROOF ENTER? Name the actual entry point — the HTTP request, the UI action, ` +
        `the device. If you invoked an internal component directly, you proved the COMPONENT, not the ` +
        `feature.\n` +
        `  2. WHAT COULD THIS CHECK NEVER SEE? List the failure modes it is blind to. If the bug class ` +
        `you are investigating is on that list, the check is invalid — no matter what it returned.\n` +
        `  3. IS THE ENABLING STATE THERE? Gated features need rows, flags, registrations, permissions. ` +
        `Count them. An empty table is the highest-signal check there is and it is the one that gets ` +
        `skipped.\n\n` +
        `Then either drive the real path and report that, or downgrade the claim to exactly what you ` +
        `proved and say plainly which layer is still unverified. An honest partial result is fine here; ` +
        `a green light that could not have gone red is not.`,
    }));
  } catch {
    // fail open — never trap a session on a guard error
  }
  process.exit(0);
});
