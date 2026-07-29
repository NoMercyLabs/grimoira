// Stop hook: a claim may not sit higher than the evidence under it.
//
// THE CLASS, stated without reference to any subsystem: a check entered below the layer that is broken
// cannot fail. Whenever the route used to verify something is not a route its user can take, the
// verification and the usage are of two different systems, and only the user's is real. Green was
// guaranteed before the check ran.
//
// This project has been bitten by that class at least six times, in six unrelated stacks, and each time
// it was recorded as its own separate lesson — proxy-instead-of-substance, mock-instead-of-collaborator,
// testbed-copy-instead-of-published-package, compiles-instead-of-runs, hand-built-record-instead-of-real
// -payload. Six instances, no rule. A per-instance memory only fires when the next situation resembles
// that instance, so a new surface walks straight past all of them. Hence one guard, at the class level,
// with nothing subsystem-specific in it.
//
// The shape is always: CLAIM ALTITUDE > EVIDENCE ALTITUDE. It compiles is not it runs. The unit passes
// is not the feature works. It works locally is not CI is green. It works in the workspace copy is not
// it works for a consumer of the published artifact. The transport sent is not the user received.
import { readFileSync, existsSync, mkdirSync, writeFileSync } from 'node:fs';
import { homedir } from 'node:os';
import { join, basename, dirname } from 'node:path';
import { tailEntries } from './brain-lib.mjs';

const MAX_BLOCKS = 2;

// An assertion that something WORKS — any layer, any stack. This is the altitude being claimed.
// Matched on the PREDICATE, never on a list of subjects. The first version enumerated the nouns that
// could be working — and then missed "the seek regression is fixed" because "regression" was not
// preceded by the exact article it expected, and missed "the docs site is functional" because "site"
// was not in the list. Enumerating instances is the very mistake this guard exists to catch, and it
// had been committed inside the guard itself. Anything at all can be the thing that works.
const WORKS_CLAIM = new RegExp([
  "\\b(is|are|'s|'re) (now |already )?(working|live|fixed|functional|resolved|operational|correct|in place|sorted)\\b",
  "\\b(works?|working) (now|again|end[- ]to[- ]end|in production|for (users|consumers|everyone|clients))\\b",
  "\\b(verified|confirmed|proven) (working|fixed|correct|end[- ]to[- ]end)\\b",
  "\\b(users?|clients?|consumers?|devices?|callers?) (now )?(get|gets|receive|receives|see|sees|can)\\b",
  "\\b(deliver(s|ed)|arriv(es|ed)|render(s|ed) correctly|plays? correctly)\\b",
  "\\b(bug|issue|regression|problem|crash|leak|failure) is (fixed|gone|resolved)\\b",
  "\\bshipped and working\\b",
].join('|'), 'i');

// Evidence one or more layers BELOW the claim. Each of these is a real proxy this project has already
// mistaken for the real thing, generalised away from the subsystem it happened in.
const PROXY_EVIDENCE = new RegExp([
  // build-time standing in for run-time
  "\\b(compiles?|compiled|builds?|built|type[- ]?checks?|type[- ]?checked|lint(s|ed)?|formats?) (clean|green|fine|ok|successfully|without error)\\b",
  "\\bno (compile|build|type|lint) errors?\\b",
  // a unit standing in for the assembled system
  "\\b(unit )?tests? (pass|passed|are green)\\b", "\\bsuite is green\\b", "\\ball tests green\\b",
  // a stand-in standing in for the collaborator
  "\\b(mock(ed|s)?|stub(bed|s)?|fake(d|s)?|in[- ]memory|synthetic|hand[- ](built|written|rolled))\\b",
  // a local or workspace copy standing in for the shipped artifact
  "\\b(locally|on my machine|in the (testbed|workspace|sandbox|dev copy)|against the (local|copied) (dist|build))\\b",
  // an internal seam standing in for the entry point
  "\\b(called|invoked|ran) (it |the )?(service|transport|handler|function|method|class|helper|repository) directly\\b",
  "\\bbypass(ing|ed)? the\\b",
  // counting standing in for checking
  "\\b(file|row|line|page) count\\b", "\\bno (errors?|warnings?) in the (log|output)\\b",
].join('|'), 'i');

// Evidence AT the claim's altitude. Deliberately spans every surface this project ships, because a
// guard that only understands HTTP would wave through every device, package and pipeline claim.
const REACH_EVIDENCE = new RegExp([
  "\\bend[- ]to[- ]end\\b", "\\bthrough the (real|full|whole) (path|flow|stack|pipeline)\\b",
  // network surfaces
  "\\b(POST|GET|PUT|PATCH|DELETE) /", "\\bcurl\\b", "\\bvia the (api|endpoint|ui|dashboard|app|hub)\\b",
  "\\breal (user|account|token|session|request)\\b",
  // client surfaces
  "\\bon (the |a )?(device|phone|tv|emulator|handset|browser|desktop app)\\b", "\\bscreenshot\\b",
  "\\blogcat\\b", "\\bin the (browser|app|player|tray|shade)\\b", "\\bclicked\\b", "\\bnavigated to\\b",
  // artifact surfaces
  "\\b(installed|consumed|imported) (the )?published\\b", "\\bfrom (npm|nuget|the registry|the package)\\b",
  "\\bfresh (install|clone|container)\\b",
  // data and job surfaces
  "\\breal (file|media|payload|json|input|database|db)\\b", "\\bagainst (production|the real)\\b",
  "\\bre-?ran the (job|migration|encode|scan)\\b",
  // pipeline surfaces
  "\\bCI (is )?green\\b", "\\bthe run (passed|succeeded)\\b", "\\bwatched the run\\b",
].join('|'), 'i');

// Naming the gap IS the honest report this guard wants. Never punish it.
const SELF_LIMITED = new RegExp([
  "\\bnot (yet )?(verified|tested|proven|exercised) (end[- ]to[- ]end|on|against|through)\\b",
  "\\bproves? the (component|unit|transport|function|layer|part), not\\b",
  "\\bhave not (driven|exercised|run|tried) the real\\b",
  "\\bstill needs? a (device|real|manual|production) (pass|run|check|test)\\b",
  "\\bthis is (a )?(proxy|partial|indirect)\\b",
  "\\bcannot (verify|confirm) (that|this) (from|without) here\\b",
  "\\bwould not (catch|see|detect)\\b",
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
    const claim = said.match(WORKS_CLAIM);
    if (!claim) process.exit(0);
    if (SELF_LIMITED.test(said)) process.exit(0);
    if (REACH_EVIDENCE.test(said)) process.exit(0);
    const proxy = said.match(PROXY_EVIDENCE);

    state.blocks += 1;
    try { mkdirSync(dirname(statePath), { recursive: true }); writeFileSync(statePath, JSON.stringify(state)); } catch { /* best effort */ }

    process.stdout.write(JSON.stringify({
      decision: 'block',
      reason:
        `You claimed something WORKS (matched: "${claim[0]}")` +
        (proxy ? `, and the evidence you named is a layer below that claim (matched: "${proxy[0]}")` : ' without naming evidence at that altitude') +
        `.\n\n` +
        `THE TEST: could the owner reproduce your verification using only what you handed him? He has the ` +
        `app, the endpoint, the device, the installed package, the running pipeline. If your check used ` +
        `a route he cannot take, your verification and his use of the thing are of two different ` +
        `systems, and only his is real.\n\n` +
        `A check entered BELOW the broken layer cannot fail. These are all the same mistake:\n` +
        `  it compiles          is not   it runs\n` +
        `  the unit passes      is not   the assembled system works\n` +
        `  the mock returned    is not   the real collaborator agrees\n` +
        `  green locally        is not   green in CI\n` +
        `  the workspace copy   is not   the published artifact a consumer installs\n` +
        `  the internal call    is not   the entry point, with everything above it in the way\n` +
        `  the count went up    is not   the content is correct\n\n` +
        `Answer three things before ending the turn:\n` +
        `  1. WHERE DID THE PROOF ENTER, and is that reachable by the person who will use this?\n` +
        `  2. WHAT COULD THIS CHECK NEVER SEE? If the failure you are investigating is on that list, ` +
        `the check is invalid whatever it returned.\n` +
        `  3. IS THE ENABLING STATE THERE? Rows, flags, registrations, permissions, published versions. ` +
        `Count them — an empty one is the highest-signal check and the one that gets skipped.\n\n` +
        `Then re-verify at the right altitude, or downgrade the claim to exactly what you proved and say ` +
        `which layer is still unverified. An honest partial result is always fine. A green light that ` +
        `could not have gone red is not.`,
    }));
  } catch {
    // fail open — never trap a session on a guard error
  }
  process.exit(0);
});
