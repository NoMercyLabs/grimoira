// Deferral detected as GRAMMAR instead of as vocabulary.
//
// continue-guard grew one regex constant per phrasing until it had seven, and its length was the bug
// report: a phrase list can only ever recognise deferrals that have already happened, so a new one
// leaks every week and the owner pays the round trip. That is the same failure it exists to police —
// fixing the instance in front of me instead of the class — committed by the guard itself.
//
// The class: a closing message DEFERS when it refers to a unit of work in a mood other than completed.
// Mood is carried by function words — modals, future auxiliaries, readiness copulas, second-person
// pronouns — and those are a CLOSED CLASS of English. They do not grow when a new defect appears.
// Phrasings are unbounded; the grammar under them is not. So the enumeration moves from
// "phrasings × verbs" (unbounded) down to "verbs" (bounded, and each entry covers every phrasing that
// uses it).
//
// Evaluated PER SENTENCE, which structurally removes a bug the phrase version needed a special case
// for: a past-tense mention anywhere in the message used to excuse a deferral somewhere else in it
// ("I've already shipped one guess today" waving through an experiment that was never run). Sentences
// are judged on their own mood, so an unrelated completed action cannot launder a pending one.

// ---------------------------------------------------------------- vocabulary (the bounded part)
// Work verbs in base form. Regular inflections (-s, -ed, -ing, -d) are matched morphologically, so one
// entry covers every tense. Speech acts (flag, note, mention, say) are deliberately absent: reporting
// is not working, and treating it as work is what made the guard block finished turns for saying
// "worth flagging that".
const BASE = [
  'fix', 'add', 'build', 'run', 'test', 'check', 'wire', 'write', 'make', 'change', 'update',
  'remove', 'delete', 'move', 'rename', 'refactor', 'implement', 'extend', 'split', 'merge',
  'migrate', 'publish', 'ship', 'commit', 'push', 'verify', 'investigate', 'trace', 'instrument',
  'log', 'patch', 'clean', 'sweep', 'cover', 'handle', 'address', 'resolve', 'tackle', 'start',
  'begin', 'finish', 'land', 'apply', 'replace', 'swap', 'port', 'document', 'record', 'capture',
  'store', 'index', 'deploy', 'release', 'revert', 'bump', 'upgrade', 'enable', 'disable', 'hook',
  'connect', 'bind', 'register', 'emit', 'render', 'compact', 'close', 'open', 'wrap', 'finalise',
  'finalize', 'validate', 'audit', 'profile', 'benchmark', 'reproduce', 'repro', 'debug', 'guess',
  'pick', 'switch', 'delete', 'restore', 'rebuild', 'retry', 'rerun', 'confirm', 'measure',
];

// Irregular pasts, which morphology cannot derive. Each maps to its base so a completed action is
// recognised as completed rather than read as a new proposal.
const IRREGULAR = {
  ran: 'run', wrote: 'write', made: 'make', built: 'build', did: 'do', began: 'begin',
  swept: 'sweep', held: 'hold', took: 'take', got: 'get', went: 'go', left: 'leave',
  found: 'find', kept: 'keep', sent: 'send', put: 'put', cut: 'cut', hit: 'hit', shut: 'shut',
  rebuilt: 'rebuild', undid: 'undo', drew: 'draw', threw: 'throw', caught: 'catch',
};

// Nouns that ARE the unit of work, for sentences with no verb at all ("worth a look on your side").
const WORK_NOUN = /\b(look|pass|fix|test|check|change|patch|sweep|refactor|migration|cleanup|rewrite|follow[- ]?up|device pass)\b/gi;

// ---------------------------------------------------------------- mood (the closed part)
const MODAL = /\b(would|will|'ll|'d|could|might|may|shall|should)\b/i;
const PERIPHRASTIC = /\b(going to|gonna|about to|plan to|planning to|intend to|want to|wanting to|hoping to|looking to|set to)\b/i;
const READINESS = /\b(ready (to|for|when|whenever)|queued|teed up|lined up|on deck|standing by|good to go|happy to|able to|waiting (on|for) you)\b/i;
const FUTURE_ADVERB = /\b(next|then|afterwards?|later|subsequently|soon|tomorrow|after (this|that)|up next|first up)\b/i;
const SECOND_PERSON = /\b(you|you'?re|you'?ll|you'?d|your|yours|yourself)\b/i;
// the owner in the past tense is a citation, not an assignment: "the rule you asked for", "the bug you
// reported". Built from the same irregular table the action tagger uses, so the two agree on what
// counts as past.
const YOU_PAST = new RegExp(
  `\\b(you|you'?ve|you'?d)\\s+(?:(?:just|already|then|also|earlier)\\s+)?(\\w+ed|${Object.keys(IRREGULAR).join('|')})\\b`,
  'gi',
);
const FIRST_PERSON = /\b(i|i'?m|i'?ll|i'?d|i'?ve|we|we'?re|we'?ll|we'?ve|my|our)\b/i;
// A state asserted as not-yet-resolved. Distinct from a plain negation: "not the remember key" is a
// finding, "not yet wired" is an open item.
const UNRESOLVED = new RegExp([
  "\\bstill (open|outstanding|broken|missing|wrong|failing|pending|to (do|go))\\b",
  "\\bnot yet\\b", "\\byet to be\\b", "\\bremains? (open|outstanding|broken|unfixed)\\b",
  "\\bleft (open|undone|unfixed)\\b", "\\bnot (fixed|done|implemented|addressed|wired|handled|covered)\\b",
  "\\bone thing (is |remains )?outstanding\\b", "\\boutstanding\\b", "\\bopen item\\b",
].join('|'), 'i');
// Necessity, not intention. "I would have to close your Chrome" explains why something did NOT happen;
// paired with a refusal in the same sentence it is a constraint, and a constraint is not a deferral.
const DEONTIC = /\b(have to|has to|had to|need to|needs to|needed to|must)\b/i;
const REFUSAL = /\b(not (doing|going to|willing)|am not|i'?m not|won'?t|will not|refuse|declined?|and i am not)\b/i;

const escape = (s) => s.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
const BASE_RE = new RegExp(`\\b(${BASE.map(escape).join('|')})(s|es|ed|ing|d)?\\b`, 'gi');
const IRREGULAR_RE = new RegExp(`\\b(${Object.keys(IRREGULAR).join('|')})\\b`, 'gi');

// Most work verbs are also nouns — a patch, a log, a record, the wrong instrument. A determiner in
// front of one puts it in noun position, where it names a thing rather than proposing an action, and
// reading it as a verb blocked the sentence "prose is the wrong instrument for it". The auxiliary
// lookahead keeps "that would have caught" out of it: "that" is a determiner, but what follows is a
// verb phrase, not a noun phrase.
const DETERMINER_BEFORE = /\b(the|a|an|this|that|these|those|my|our|your|its|their|his|her|no|any|some|each|every|another)\s+(?!(?:would|will|have|had|has|be|been|is|are|was|were|do|does|did|can|could|should|might|may|must|to)\b)([\w-]+\s+)?$/i;

// A hyphen glues a word into a compound, and a compound is a name. "the symptom-patch rule" contains
// no proposal to patch anything — but the hyphen also broke the determiner lookback, because the
// nearest whitespace-delimited word before "patch" was "symptom-" rather than "the". A verb welded
// into a compound is never the sentence's action, whichever side the hyphen falls on.
const compound = (sentence, at, len) => sentence[at - 1] === '-' || sentence[at + len] === '-';

// A work noun glued into a compound names a concept, not a job. "the symptom-patch rule" is a term of
// art; "worth a look on your side" is an assignment. The verb tagger already made this distinction and
// the noun gate did not, which let a hyphenated term walk in as the sentence's unit of work.
function hasWorkNoun(sentence) {
  for (const m of sentence.matchAll(WORK_NOUN)) {
    if (!compound(sentence, m.index, m[0].length)) return true;
  }
  return false;
}

// Every work action in a sentence, tagged with whether it is completed or not.
export function actions(sentence) {
  const found = [];
  const nounish = (m) => compound(sentence, m.index, m[0].length)
    || DETERMINER_BEFORE.test(sentence.slice(Math.max(0, m.index - 40), m.index));
  for (const m of sentence.matchAll(BASE_RE)) {
    if (nounish(m)) continue;
    const suffix = (m[2] || '').toLowerCase();
    found.push({ token: m[0], base: m[1].toLowerCase(), past: suffix === 'ed' || suffix === 'd' });
  }
  for (const m of sentence.matchAll(IRREGULAR_RE)) {
    if (nounish(m)) continue;
    found.push({ token: m[0], base: IRREGULAR[m[0].toLowerCase()], past: true });
  }
  return found;
}

// A modal perfect is IRREALIS: "the fix that would have caught all four bugs" describes something that
// never happened, and it is the natural way to argue for work while not doing it. Reading its past
// participle as a completed action let the single most expensive shape — a fix identified, argued for
// and left unbuilt — score as finished work.
const MODAL_PERFECT = /\b(would|could|should|might|may|must|will|'d|'ll)\s+(have|had|'ve)\b/i;

// Completed and owned: a past-tense work action attributed to me, or with the subject dropped, which
// is the terse register the owner asks for ("Shipped.", "Recorded it as an instance.").
function completed(sentence, acts) {
  if (!acts.some((a) => a.past)) return false;
  if (MODAL_PERFECT.test(sentence)) return false;
  // "you fixed" / "they shipped" is someone else's completion and says nothing about mine.
  const other = /\b(you|he|she|they|it)\s+\w{0,12}(ed|ran|wrote|made|built|did)\b/i.test(sentence);
  return !other;
}

export function splitSentences(text) {
  return String(text)
    .split(/(?<=[.!?;:])\s+|\n+/)
    .map((s) => s.trim())
    .filter(Boolean);
}

// One sentence's verdict, plus the two-sentence window used only for locating the owner of an
// unresolved state ("Still open: X. Worth a look on your side.") — the state and the hand-off are
// routinely split across a full stop, and requiring both in one sentence missed that shape entirely.
function judge(sentence, next) {
  const acts = actions(sentence);
  const window = next ? `${sentence} ${next}` : sentence;

  if (UNRESOLVED.test(sentence) && (SECOND_PERSON.test(window) || FIRST_PERSON.test(window))
      && !completed(sentence, acts)) {
    return { defer: true, why: 'an unresolved item handed to someone', hit: sentence.slice(0, 90) };
  }

  if (!acts.length && !hasWorkNoun(sentence)) return { defer: false };
  if (completed(sentence, acts)) return { defer: false };

  // A constraint states why something cannot be done. It is the one modal construction that is not a
  // proposal, and it needs the refusal to qualify — "I'd need to run the migration" on its own is a
  // proposal wearing a constraint's clothes.
  if (MODAL.test(sentence) && DEONTIC.test(sentence) && REFUSAL.test(sentence)) return { defer: false };

  // "the rule you asked for" refers back to a past request; it does not point work at anyone. Second
  // person only means hand-off when the owner is the agent of something still pending, so retrospective
  // references to what he already said are removed before the pronoun is allowed to accuse anything.
  const pending = sentence.replace(YOU_PAST, ' ');

  const mood =
    (MODAL.test(sentence) && 'a modal') ||
    (PERIPHRASTIC.test(sentence) && 'an announced intention') ||
    (READINESS.test(sentence) && 'work offered as ready') ||
    (FUTURE_ADVERB.test(sentence) && 'a future adverbial') ||
    (SECOND_PERSON.test(pending) && 'work pointed at the owner') ||
    null;
  if (!mood) return { defer: false };
  return { defer: true, why: `${mood} over unfinished work`, hit: sentence.slice(0, 90) };
}

// A phrase MENTIONED is not a phrase USED. Reporting on a guard means quoting what it catches, and
// without this the guard cannot be described without blocking the description. Quoted spans, code
// spans and table rows carry examples and data, never the closing statement that parks work.
export function stripMentions(text) {
  return String(text)
    .replace(/```[\s\S]*?```/g, ' ')
    .replace(/`[^`\n]*`/g, ' ')
    .replace(/"[^"\n]{0,120}"/g, ' ')
    .replace(/[“][^”\n]{0,120}[”]/g, ' ')
    .replace(/^\s*\|.*$/gm, ' ');
}

// The message defers if any sentence in it does. Returns the first offending sentence, or null.
export function classify(raw) {
  const text = stripMentions(raw);
  const sentences = splitSentences(text);
  for (let i = 0; i < sentences.length; i++) {
    const v = judge(sentences[i], sentences[i + 1]);
    if (v.defer) return v;
  }
  return null;
}
