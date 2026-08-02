// Stop-hook guard: the unit of work is the POPULATION, never the sample you can reach.
//
// One failure, three faces, all of which the owner has had to name out loud more than once:
//
//   SITES        — he reports a bug in one place, the same construct is wrong in six, the patch lands
//                  on the one he pointed at. Worse, the patch sometimes pins the general mechanism to
//                  that one case, which actively breaks the others (three spoken cues, all with real
//                  DOM targets, all pinned onto the H1).
//   SHAPES       — a detector grows a new arm per phrasing instead of matching the property. Its
//                  length is the bug report. blast-radius.mjs watches that one at edit time.
//   REQUIREMENTS — a 50-page spec asks for a 1:1 port; a fraction of it gets built and verified, and
//                  the report says "all good". His words: "you ignored 95% of the goal and called it
//                  done." This is the costliest face, because the scope of the REPORT was set by the
//                  scope of the WORK instead of by the scope of the ASK.
//
// Four memories already state the principle in plain English and it still fails every session, which
// is the evidence that prose is the wrong instrument. A rule gets consulted when you remember to
// consult it, and not remembering is the entire failure mode. So it lives here instead.
//
// Two detectors, matching the two moments the sample gets substituted for the population:
//
//   A. FIXED THE INSTANCE. A defect report naming a concrete thing, edits made, and no repo-wide
//      search anywhere in the turn. The absence of the search IS the finding: it is the mechanical
//      proof that the example was read as the specification.
//   B. CLAIMED DONE WITHOUT A DENOMINATOR. The ask references an enumerable population — a spec, a
//      ledger, "every", "all", "1 to 1" — and the closing report asserts completion without stating
//      the count it is claiming against. "Done" over an enumerable goal is a fraction or it is a
//      guess, and an unstated fraction always reads as 1/1.
//
// Distinct from proof-guard, which polices the ALTITUDE of evidence ("it compiles" is not "it runs").
// This one polices the COVERAGE of scope ("this part passes" is not "all of it passes"). A claim can
// be at the right altitude and still cover 5% of the ask.
//
// Never deadlocks: stands down after MAX_BLOCKS. Fails open on any error.
import { readFileSync, existsSync, writeFileSync, mkdirSync } from 'node:fs';
import { homedir } from 'node:os';
import { join, basename, dirname } from 'node:path';
import { tailEntries } from './brain-lib.mjs';

const MAX_BLOCKS = 2;
const MAX_EDITED_FILES = 3;   // above this a sweep plainly happened, search evidence or not

// Something is wrong and he is telling me about it. Bug reports are exactly where one instance stands
// in for many, because he names the one he happened to SEE.
const DEFECT = new RegExp([
  "\\b(broken|breaks|broke)\\b", "\\bnot working\\b", "\\bdoes ?n[o']t work\\b", "\\bis ?n[o']t working\\b",
  "\\bfail(s|ing|ed)?\\b", "\\bwrong\\b", "\\bbug\\b", "\\berror\\b", "\\bcrash(es|ed|ing)?\\b",
  "\\bregression\\b", "\\bstill\\b.{0,30}\\b(wrong|broken|missing|failing|happening)\\b",
  "\\bwhy (is|does|do|are|did)\\b", "\\bshould(n'?t)? (be|have|show|work)\\b",
  "\\bdoes ?n[o']t (show|render|fire|trigger|load|appear|update|scroll|focus)\\b",
  "\\bmissing\\b", "\\bno (scroll|focus|highlight|sound|audio|response)\\b",
  "\\bnothing happens\\b", "\\bagain\\b", "\\bstructural (error|issue|problem)\\b",
].join('|'), 'i');

// A concrete thing to laser onto: a path, a file, a code span, an identifier, a screenshot.
const ANCHOR = new RegExp([
  "`[^`\\n]+`",
  "\\b[\\w.-]+\\.(ts|tsx|js|mjs|cjs|vue|kt|kts|cs|swift|go|rs|rb|php|py|scss|css|xml|json|md|ps1|sh)\\b",
  "\\b[a-z]+[A-Z][A-Za-z]{2,}\\b",
  "[\\w-]+/[\\w./-]+",
].join('|'));

// The ask names a population rather than a thing: a spec, a ledger, or a totality word.
const ENUMERABLE_GOAL = new RegExp([
  "\\b1 ?(to|:) ?1\\b", "\\bone ?(to|:) ?one\\b", "\\bexact (replica|copy|match|parity)\\b",
  "\\bparity\\b", "\\bfull (port|coverage|surface|parity|sweep)\\b",
  "\\bevery\\b", "\\ball of (it|them|the)\\b", "\\bthe whole (thing|job|spec|surface|list|lot)\\b",
  "\\bspec(ification)?\\b", "\\bledger\\b", "\\bchecklist\\b", "\\bbacklog\\b",
  "\\beach (one|item|file|entry|method|case)\\b", "\\bacross the board\\b", "\\bcomplete(ly)? port\\b",
].join('|'), 'i');

// The closing report says the work is finished.
const COMPLETION = new RegExp([
  "\\b(all good|all done|that'?s (it|everything|all)|everything (works|passes|is (done|green)))\\b",
  "\\b(is|are|it'?s) (now )?(done|complete|finished|covered|in place|at parity)\\b",
  "\\b(done|complete|finished|shipped|landed)\\.", "\\bfully (covered|ported|implemented|migrated)\\b",
  "\\bnothing (else|more) (to do|outstanding|left)\\b", "\\bmatches the (spec|reference|original|web)\\b",
  "\\bport(ed)? (is )?complete\\b", "\\bno (gaps|remaining|outstanding)\\b",
].join('|'), 'i');

// A stated fraction of the population: "34 of 210", "34/210", "176 remaining", "12 unread".
const DENOMINATOR = new RegExp([
  "\\b\\d+\\s*(?:of|/|out of)\\s*\\d+\\b",
  "\\b\\d+\\s+(remaining|left|outstanding|unread|unclassified|gap|gaps|todo|pending|missing|uncovered)\\b",
  "\\b(remaining|outstanding|unread|unclassified|gaps?|missing|uncovered)\\s*[:=]\\s*\\d+",
  "\\ball \\d+\\b", "\\b\\d+\\s*/\\s*\\d+\\b", "\\b\\d+ (of the|out of the) \\d+\\b",
].join('|'), 'i');

const SEARCH_TOOLS = /^(Grep|Glob|Agent|Task)$/;
const SEARCH_MCP = /^mcp__aitm__(impact|fact|recall|doc|open_findings|brain_)/;
const SEARCH_SHELL = /\b(rg|ripgrep|grep|egrep|findstr|Select-String|ack|ag)\b/i;
const EDIT_TOOLS = /^(Write|Edit|MultiEdit|NotebookEdit)$/;

function turnSlice(transcriptPath) {
  const { entries } = tailEntries(transcriptPath);
  let start = 0;
  for (let i = entries.length - 1; i >= 0; i--) {
    const e = entries[i];
    const isUser = e.type === 'user' || e.message?.role === 'user';
    if (!isUser) continue;
    const c = e.message?.content;
    if (Array.isArray(c) && c.some((b) => b.type === 'tool_result')) continue;
    start = i;
    break;
  }
  return { entries, turn: entries.slice(start), promptIndex: start };
}

const blocksOf = (entries) => {
  const out = [];
  for (const e of entries) {
    const c = e.message?.content;
    if (Array.isArray(c)) out.push(...c);
    else if (typeof c === 'string') out.push({ type: 'text', text: c });
  }
  return out;
};

// Every user message in the turn, not only the first: he corrects mid-turn, and the correction is
// usually where the real scope lives.
export function userText(turn) {
  const parts = [];
  for (const e of turn) {
    if (!(e.type === 'user' || e.message?.role === 'user')) continue;
    const c = e.message?.content;
    if (typeof c === 'string') parts.push(c);
    else if (Array.isArray(c)) {
      for (const b of c) {
        if (b.type === 'text') parts.push(b.text);
        if (b.type === 'image') parts.push(' [screenshot] ');
      }
    }
  }
  return parts.join('\n');
}

export function toolUse(turn) {
  const edited = new Set();
  let searched = false;
  for (const b of blocksOf(turn)) {
    if (b.type !== 'tool_use') continue;
    if (EDIT_TOOLS.test(b.name) && b.input?.file_path) edited.add(b.input.file_path);
    if (SEARCH_TOOLS.test(b.name) || SEARCH_MCP.test(b.name)) searched = true;
    if (/^(Bash|PowerShell)$/.test(b.name) && SEARCH_SHELL.test(b.input?.command || '')) searched = true;
  }
  return { edited: [...edited], searched };
}

export function verdict({ prompt, finalText, edited, searched }) {
  const out = [];
  const hasImage = /\[screenshot\]/.test(prompt);

  if (DEFECT.test(prompt) && (ANCHOR.test(prompt) || hasImage)
      && edited.length > 0 && edited.length <= MAX_EDITED_FILES && !searched) {
    out.push(
      `You fixed the instance the owner pointed at and never looked for the others. This turn edited `
      + `${edited.length} file(s) in response to a defect report and ran no repo-wide search at all — `
      + `no Grep, no Glob, no rg, no aitm impact query. That absence is the finding: it is the `
      + `mechanical signature of reading his example as the specification.\n\n`
      + `His example is a SAMPLE FROM A POPULATION, never the population. He names the one instance he `
      + `happened to see; the defect has a shape, and the shape has siblings. A fix scoped to the one `
      + `he named leaves the rest broken and guarantees the same report comes back wearing different `
      + `clothes — which is the thing he has asked most often to stop.\n\n`
      + `Worse, and this has already happened: pinning the general mechanism to the one case you were `
      + `shown can BREAK the siblings that were working. Fixing where the symptom surfaced instead of `
      + `where it originates is how a patch makes two of three cases worse.\n\n`
      + `Do this now, not next turn: search for the construct across the repo, count the instances, `
      + `fix them in this pass, and say how many there were. If it genuinely is the only one, say that `
      + `— but say it because you looked, not because you assumed. Do NOT satisfy this guard by `
      + `asserting you already considered it; run the search.`,
    );
  }

  if (ENUMERABLE_GOAL.test(prompt) && COMPLETION.test(finalText) && !DENOMINATOR.test(finalText)) {
    out.push(
      `You claimed completion against an enumerable goal without stating the count. The ask references `
      + `a population — a spec, a ledger, a parity target, an "every" — and the report asserts the work `
      + `is done with no fraction attached.\n\n`
      + `Over an enumerable goal, "done" is a fraction or it is a guess, and an unstated fraction always `
      + `reads as 1/1. That is exactly how "all good" was delivered over 5% of a 50-page port spec: the `
      + `scope of the REPORT got set by the scope of the WORK, when it must be set by the scope of the `
      + `ASK.\n\n`
      + `Go and get the denominator from the artifact itself — the spec, the ledger, the API surface, `
      + `the checklist — not from memory and not from what you happened to touch. Then state it as `
      + `"N of M, with M-N outstanding" and name what the outstanding ones are. If the denominator is `
      + `genuinely unknowable, say that plainly instead of implying totality.`,
    );
  }
  return out;
}

// Only wire stdin when this file IS the process. A hook module registers its handler at top level, so
// importing one runs it — that already cost a debugging session here once, when a test that imported a
// guard exited the moment its own stdin closed.
const isEntryPoint = process.argv[1] && import.meta.url.endsWith(basename(process.argv[1]));

let input = '';
if (isEntryPoint) process.stdin.on('data', (d) => { input += d; });
if (isEntryPoint) process.stdin.on('end', () => {
  try {
    const payload = JSON.parse(input || '{}');
    const tp = payload.transcript_path;
    if (!tp || !existsSync(tp)) process.exit(0);

    const proj = process.env.CLAUDE_PROJECT_DIR || payload.cwd || process.cwd();
    const slug = basename(proj.replace(/[\\/]+$/, '')).toLowerCase().replace(/[^a-z0-9_-]/g, '') || 'default';
    const statePath = join(homedir(), '.aitm', slug, 'population-guard.json');
    let state = { sid: null, blocks: 0 };
    try { state = JSON.parse(readFileSync(statePath, 'utf8')); } catch { /* first run */ }
    if (state.sid !== payload.session_id) state = { sid: payload.session_id, blocks: 0 };
    if (state.blocks >= MAX_BLOCKS) process.exit(0);

    const { entries, turn } = turnSlice(tp);
    const prompt = userText(turn);
    if (!prompt.trim()) process.exit(0);
    const { edited, searched } = toolUse(turn);

    let finalText = '';
    for (let i = entries.length - 1; i >= 0; i--) {
      const e = entries[i];
      if (!(e.type === 'assistant' || e.message?.role === 'assistant')) continue;
      const c = e.message?.content;
      if (!Array.isArray(c)) continue;
      const t = c.filter((b) => b.type === 'text').map((b) => b.text).join('').trim();
      if (t.length > 0) { finalText = t; break; }
    }

    const reasons = verdict({ prompt, finalText, edited, searched });
    if (!reasons.length) process.exit(0);

    state.blocks += 1;
    try { mkdirSync(dirname(statePath), { recursive: true }); writeFileSync(statePath, JSON.stringify(state)); } catch { /* best effort */ }

    process.stdout.write(JSON.stringify({ decision: 'block', reason: reasons.join('\n\n---\n\n') }));
  } catch {
    // fail open — never trap a session on a guard error
  }
  process.exit(0);
});
