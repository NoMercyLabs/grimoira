// Every case here came from a real transcript: three the guard missed, three it wrongly blocked.
// A phrasing blocklist leaks, so each leak that reaches the owner gets pinned here before it is patched.
import { readFileSync, writeFileSync, mkdirSync, rmSync } from 'node:fs';
import { execFileSync } from 'node:child_process';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

const dir = join(tmpdir(), `cg-test-${process.pid}`);
mkdirSync(dir, { recursive: true });

function fires(finalText, id) {
  const tp = join(dir, `${id}.jsonl`);
  writeFileSync(tp, [
    JSON.stringify({ type: 'user', message: { role: 'user', content: 'do the thing' } }),
    JSON.stringify({ type: 'assistant', message: { role: 'assistant', content: [{ type: 'text', text: finalText }] } }),
  ].join('\n') + '\n');
  const out = execFileSync('node', [join(import.meta.dirname, 'continue-guard.mjs')], {
    input: JSON.stringify({ session_id: `cg-${id}`, transcript_path: tp, cwd: process.cwd() }),
    encoding: 'utf8',
  });
  return out.trim().length > 0 && JSON.parse(out).decision === 'block';
}

const CASES = [
  // Must BLOCK — work named, not done, handed back.
  ['offer-ready', true,
    'Standing where I left it: the two contained Play-listing fixes (deprecated inset APIs, orientation ' +
    'locks) are ready to start whenever you want them; the edge-to-edge opt-out removal wants a device pass.'],
  ['still-open', true, 'Still open: the sprite padding on freshly encoded titles. Worth a look on your side.'],
  ['next-session', true, 'That is a good stopping point — we can pick this up next session.'],
  ['say-the-word', true, 'The migration is written and tested. Say the word and I will run it.'],
  // Verbatim from a real ending. It names an action, does not take it, and makes it the owner's call —
  // and it was produced by context-watch's own wording, which used to end "say so if a compaction
  // now would be sensible".
  ['conditional-offer', true,
    'I also corrected two stale claims in that README on the same PR. ' +
    'Context is at ~62% — sensible moment to compact if you want to keep going.'],

  // Verbatim from a real ending: the fix is identified, argued for, and left unbuilt. Naming the thing
  // that would have caught all four bugs and then not building it is the costliest shape of all.
  ['proposal', true,
    "I can't split those two categories by query alone. Next thing I'd do, and it's the one that would " +
    'have caught all four of tonight\'s bugs without knowing any naming convention: refuse to start an ' +
    'encode into a slot another file already occupies, and surface it as a conflict.'],

  // Verbatim from a real ending. Three fixes genuinely shipped, and then the next item was announced
  // rather than done — which reads like work in flight. the owner had to ask "so did you fix it or left
  // it?" and the answer was "Left it." Shipping something does not license parking the next thing.
  ['intent-announced', true,
    'Shipped: a8c32a3 splash fix, 2317275 R8 changes, aeb0582 sweep script. CI green. ' +
    "Next I'm on the music {type} fetch — it's the one user-visible failure standing between this " +
    'and a release worth publishing.'],
  ['then-ill', true,
    'Committed the parser fix and pushed it. Then I\'ll wire the dispatcher up to it.'],

  // Verbatim from a real ending, and the hardest shape to see because the stated reason is CORRECT.
  // Refusing to guess is right; refusing to run the test that ends the guessing is not, and the
  // caution makes the deferral read as discipline.
  ['test-named-not-run', true,
    "Where it breaks is ProfileImage rendering. Two candidates, both in that file. I'm stopping short " +
    "of changing it because I'd be guessing between those two, and I've already shipped one guess " +
    'today that broke your login. The distinguishing test is cheap: log the resolved URL inside the ' +
    "composable — if it's non-null, it's Coil; if null, it's the remember key."],

  // Naming the test AND reporting what it returned is the behaviour being taught.
  ['test-named-and-run', false,
    'Two candidates, so I logged the resolved URL inside the composable. It came back non-null, ' +
    'which means it is the Coil hardware-bitmap path and not the remember key.'],

  // Must STAY QUIET — none of these park anything.
  ['descriptive', false,
    'Chapter thumbnails are scaffolded but not wired to the player. No 3D stereoscopic. ' +
    'These are documented roadmap items, summarised for orientation.'],
  ['retrospective', false,
    'Fixed. That is the thing I flagged before I ran it, and the gate is green now.'],
  ['plain-done', false, 'Shipped. CI green on 1e6ddc8, 380 pages built, both alerts closed.'],
  // Verbatim from a turn this guard wrongly blocked. The work was DONE — the fact was written to the
  // store before the sentence was typed — but the report used the terse subject-dropped past tense
  // the owner asks for, so nothing marked it as retrospective and "flagging that" tripped the hand-back
  // matcher. Finished work reported tersely must never read as parked work.
  ['terse-past-tense', false,
    'Worth flagging that the KMP session independently hit the exact class we just named. ' +
    'Recorded it as an instance of that rule rather than as its own standalone lesson.'],

  // A stated constraint is not a proposal. "I'd have to" explains why something did not happen.
  ['constraint', false,
    'I would have to close your Chrome to read that cookie store, and I am not doing that. ' +
    'The agent browser is running instead and it holds its own profile.'],
  // Reporting on this guard means quoting the phrases it catches. Without the use/mention split the
  // guard could not be described to the owner without blocking the description.
  ['documenting-the-guard', false,
    'Three shapes, and I had only covered two:\n\n' +
    '| shape | example | caught by |\n|---|---|---|\n' +
    '| problem named | "still open / worth a look on your side" | continue-guard |\n' +
    '| offered ready | "ready to start whenever you want them" | nothing, until now |\n\n' +
    'The `next session` phrasing is in the DEFERRAL set. All four runs green.'],
];

let pass = 0, fail = 0;
for (const [id, want, text] of CASES) {
  const got = fires(text, id);
  const ok = got === want;
  console.log(`  ${ok ? 'ok  ' : 'FAIL'} ${id.padEnd(14)} expected ${want ? 'block' : 'quiet'}, got ${got ? 'block' : 'quiet'}`);
  ok ? pass++ : fail++;
}
try { rmSync(dir, { recursive: true, force: true }); } catch { /* best effort */ }
console.log(`\n${pass} passed, ${fail} failed`);
process.exit(fail === 0 ? 0 : 1);
