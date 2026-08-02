// PostToolUse guard on edits: the two mechanical fingerprints of a symptom patch.
//
// the owner has said the same thing in four separate memories — "do NOT laser-focus on the single
// instance" — and it still fails every session, which is the proof that a written rule is the wrong
// instrument for it. A rule is consulted when you remember to consult it, and the whole failure mode
// is not remembering that the thing in front of you is one of many. So it moves into the tool trace,
// where it cannot be skipped by not thinking of it.
//
// Two fingerprints, both language-agnostic, both cheap:
//
//   1. ENUMERATION GROWTH. You are adding the Nth item to a list of structurally identical siblings.
//      A detector that needs a new arm every week is not a detector with a missing arm; it is the
//      wrong shape, and the list is the bug report. This file's own ancestor case: continue-guard
//      accumulated seven regex constants for seven phrasings of one property.
//
//   2. UNFIXED SIBLINGS. The edit removed a construct that still exists in other files. If removing
//      it here was a fix, every other occurrence is the same defect, untouched. This is "the issue
//      is all over the place and has many forms" reduced to something greppable.
//
// Advisory, never blocking: both shapes have legitimate cases (a new locale, a real new branch), and
// a wall would train avoidance rather than attention. It injects context into the turn instead, which
// cannot be missed the way a memory can. Fails open on anything unexpected.
import { readFileSync, writeFileSync, mkdirSync, existsSync, readdirSync, statSync } from 'node:fs';
import { homedir } from 'node:os';
import { join, dirname, basename, resolve, relative, sep } from 'node:path';

const MIN_SIBLINGS = 5;        // fires as you add the fifth — four of a shape is a pattern, not a coincidence
const MAX_REPORT_FILES = 6;
const WALK_BUDGET_MS = 1500;
const MAX_WALK_FILES = 4000;

// Lists that are SUPPOSED to grow one item at a time. A locale file, a test table and a migrations
// directory are enumerations by design; flagging them would make the guard noise, and noise is how a
// guard gets ignored. Data formats are excluded for the same reason.
const EXEMPT_PATH = new RegExp(
  '(^|[\\\\/])(tests?|__tests__|__fixtures__|fixtures|mocks?|snapshots?|locales?|lang|i18n|migrations|seeds?)([\\\\/]|$)'
  + '|\\.(test|spec|stories|bench|d)\\.[a-z]+$'
  + '|\\.(json|jsonl|ndjson|csv|tsv|lock|snap|po|pot|xlf|resx|md|mdx|mdc|txt|rst|yml|yaml|toml|ini|env|sql|svg|lock)$',
  'i',
);

const SKIP_DIR = new Set([
  '.git', 'node_modules', 'dist', 'build', 'out', 'bin', 'obj', 'target', 'vendor', 'coverage',
  '.next', '.nuxt', '.turbo', '.scratch', '.gradle', '.idea', '.vs', '.venv', 'venv', '__pycache__',
  'Pods', 'DerivedData', 'bin-cli', 'packages-lock',
]);

const TEXT_EXT = new Set([
  'js', 'mjs', 'cjs', 'jsx', 'ts', 'tsx', 'vue', 'svelte', 'cs', 'kt', 'kts', 'java', 'swift', 'go',
  'rs', 'rb', 'php', 'py', 'sh', 'ps1', 'psm1', 'bat', 'c', 'h', 'cpp', 'hpp', 'm', 'mm', 'scss',
  'css', 'less', 'gradle', 'xml', 'razor', 'cshtml', 'blade', 'twig', 'astro',
]);

// Words too common to carry a blast radius. Frequency lists are closed by nature, unlike phrase lists:
// this one does not need a new entry every time a new defect shows up.
const COMMON = new Set([
  'return', 'function', 'const', 'string', 'number', 'object', 'boolean', 'import', 'export',
  'require', 'length', 'console', 'undefined', 'default', 'public', 'private', 'static', 'class',
  'extends', 'implements', 'interface', 'package', 'namespace', 'foreach', 'options', 'params',
  'result', 'results', 'values', 'buffer', 'stream', 'nullable', 'internal', 'protected', 'override',
  'virtual', 'abstract', 'readonly', 'continue', 'process', 'context', 'content', 'element',
]);

// A line reduced to its shape: literals, numbers and names erased, punctuation and arity kept. Two
// lines with the same skeleton are the same construct with different contents — which is exactly what
// "another arm on the same list" means, in any language, without knowing the language.
export function skeleton(line) {
  let s = String(line).trim();
  if (!s) return null;
  s = s.replace(/(['"`])(?:\\[\s\S]|(?!\1)[\s\S])*?\1/g, '§');
  s = s.replace(/\b\d[\w.]*\b/g, '#');
  s = s.replace(/[A-Za-z_$][\w$]{2,}/g, '_');
  s = s.replace(/\s+/g, ' ').trim();
  // A skeleton that is mostly punctuation describes every closing brace in the file. Require some
  // substance before it is allowed to accuse anything.
  const slots = (s.match(/[_§#]/g) || []).length;
  if (s.length < 8 || slots < 2) return null;
  return s;
}

// Prose is not a construct. The first live firing of this guard accused the word "matched" inside a
// template literal — an English word in a message string, present in seven other files for the same
// unremarkable reason. String literals and comments carry sentences; code carries identifiers, so the
// text is stripped of both before anything is called a token.
function codeOnly(s) {
  return String(s)
    .replace(/`(?:\\[\s\S]|[^`\\])*`/g, ' ')
    .replace(/'(?:\\[\s\S]|[^'\\\n])*'/g, ' ')
    .replace(/"(?:\\[\s\S]|[^"\\\n])*"/g, ' ')
    .replace(/\/\*[\s\S]*?\*\//g, ' ')
    .replace(/(^|[^:])\/\/[^\n]*/g, '$1 ')
    .replace(/^\s*#[^\n]*/gm, ' ');
}

// What the edit took away. A token present before and absent after is the thing being replaced, and
// its other occurrences are the unfixed instances. Tokens still present in the new text are context,
// not the subject of the change.
export function removedTokens(oldStr, newStr) {
  const grab = (s) => new Set(codeOnly(s).match(/[A-Za-z_$][\w$]*(?:\.[A-Za-z_$][\w$]*)*/g) || []);
  const after = grab(newStr);
  return [...grab(oldStr)]
    .filter((t) => !after.has(t)
      && t.length >= 6
      && /[a-z]/i.test(t)
      && !COMMON.has(t.toLowerCase())
      && !t.split('.').every((p) => COMMON.has(p.toLowerCase())))
    .sort((a, b) => b.length - a.length)
    .slice(0, 3);
}

export function addedLines(oldStr, newStr) {
  const before = new Set(String(oldStr).split(/\r?\n/).map((l) => l.trim()));
  return String(newStr).split(/\r?\n/).filter((l) => l.trim() && !before.has(l.trim()));
}

export function countSkeleton(fileText, skel) {
  let n = 0;
  for (const line of String(fileText).split(/\r?\n/)) if (skeleton(line) === skel) n++;
  return n;
}

function repoRoot(from) {
  let dir = dirname(resolve(from));
  for (let i = 0; i < 40; i++) {
    if (existsSync(join(dir, '.git'))) return dir;
    const up = dirname(dir);
    if (up === dir) break;
    dir = up;
  }
  return dirname(resolve(from));
}

function* walk(root, deadline) {
  const stack = [root];
  let seen = 0;
  while (stack.length) {
    if (Date.now() > deadline || seen > MAX_WALK_FILES) return;
    const dir = stack.pop();
    let items;
    try { items = readdirSync(dir, { withFileTypes: true }); } catch { continue; }
    for (const it of items) {
      if (it.isDirectory()) {
        if (!SKIP_DIR.has(it.name) && !it.name.startsWith('.')) stack.push(join(dir, it.name));
        continue;
      }
      const ext = it.name.includes('.') ? it.name.split('.').pop().toLowerCase() : '';
      if (!TEXT_EXT.has(ext)) continue;
      seen++;
      yield join(dir, it.name);
    }
  }
}

// Other files still carrying the token. Bounded by time and count: a guard that stalls an edit is a
// guard that gets turned off.
export function otherSites(token, editedFile, root, budgetMs = WALK_BUDGET_MS) {
  const deadline = Date.now() + budgetMs;
  const edited = resolve(editedFile);
  const hits = [];
  for (const f of walk(root, deadline)) {
    if (resolve(f) === edited) continue;
    let text;
    try {
      const st = statSync(f);
      if (st.size > 400_000) continue;
      text = readFileSync(f, 'utf8');
    } catch { continue; }
    if (text.includes(token)) {
      hits.push(f);
      if (hits.length >= 40) break;
    }
  }
  return hits;
}

function editsOf(payload) {
  const ti = payload.tool_input || {};
  if (Array.isArray(ti.edits)) return ti.edits.map((e) => ({ old: e.old_string ?? '', neu: e.new_string ?? '' }));
  if (typeof ti.old_string === 'string') return [{ old: ti.old_string, neu: ti.new_string ?? '' }];
  return [];
}

function seenStore(payload) {
  const proj = process.env.CLAUDE_PROJECT_DIR || payload.cwd || process.cwd();
  const slug = basename(proj.replace(/[\\/]+$/, '')).toLowerCase().replace(/[^a-z0-9_-]/g, '') || 'default';
  const path = join(homedir(), '.aitm', slug, 'blast-radius.json');
  let state = { sid: null, seen: [] };
  try { state = JSON.parse(readFileSync(path, 'utf8')); } catch { /* first run */ }
  if (state.sid !== payload.session_id) state = { sid: payload.session_id, seen: [] };
  return {
    fresh: (key) => !state.seen.includes(key),
    mark: (key) => {
      state.seen.push(key);
      try { mkdirSync(dirname(path), { recursive: true }); writeFileSync(path, JSON.stringify(state)); } catch { /* best effort */ }
    },
  };
}

export function analyse(payload, { readFile = readFileSync, search = otherSites } = {}) {
  const file = payload.tool_input?.file_path;
  if (!file) return [];
  const edits = editsOf(payload);
  if (!edits.length) return [];

  const notes = [];
  const root = repoRoot(file);
  const rel = (p) => relative(root, p).split(sep).join('/');

  // 1. Enumeration growth — same file, same shape, already several of them.
  if (!EXEMPT_PATH.test(file)) {
    let text = '';
    try { text = readFile(file, 'utf8'); } catch { text = ''; }
    if (text) {
      const counted = new Map();
      for (const e of edits) {
        for (const line of addedLines(e.old, e.neu)) {
          const skel = skeleton(line);
          if (!skel || counted.has(skel)) continue;
          const n = countSkeleton(text, skel);
          counted.set(skel, n);
          if (n >= MIN_SIBLINGS) {
            notes.push({
              kind: 'enumeration',
              key: `enum:${rel(file)}:${skel}`,
              text:
                `You just added item ${n} to a run of ${n} structurally identical lines in ${rel(file)}:\n`
                + `    ${line.trim().slice(0, 140)}\n`
                + `A list that needs a new entry every time the problem reappears is not a list with a `
                + `missing entry — it is the wrong shape, and its length is the bug report. Before adding `
                + `the next one, answer: what GENERATES these ${n} cases, and can one mechanism cover them `
                + `all? If they are genuinely irreducible data, fine — say so once and move on.`,
            });
          }
        }
      }
    }
  }

  // 2. Unfixed siblings — the construct this edit removed still lives elsewhere.
  const tokens = new Set();
  for (const e of edits) for (const t of removedTokens(e.old, e.neu)) tokens.add(t);
  for (const token of [...tokens].slice(0, 2)) {
    const hits = search(token, file, root);
    if (!hits.length) continue;
    const list = hits.slice(0, MAX_REPORT_FILES).map((h) => `    ${rel(h)}`).join('\n');
    const more = hits.length > MAX_REPORT_FILES ? `\n    …and ${hits.length - MAX_REPORT_FILES} more` : '';
    notes.push({
      kind: 'siblings',
      key: `sib:${token}`,
      text:
        `You replaced \`${token}\` in ${rel(file)}. It still appears in ${hits.length} other file(s):\n`
        + `${list}${more}\n`
        + `If changing it here was a fix, those are the same defect in different clothes. Check them in `
        + `this pass — one instance repaired and its siblings left standing is the outcome the owner has `
        + `asked most often to stop. If they are genuinely different uses, confirm that and say so.`,
    });
  }
  return notes;
}

if (process.argv[1] && import.meta.url.endsWith(basename(process.argv[1]))) {
  let input = '';
  process.stdin.on('data', (d) => { input += d; });
  process.stdin.on('end', () => {
    try {
      const payload = JSON.parse(input || '{}');
      const notes = analyse(payload);
      if (notes.length) {
        const store = seenStore(payload);
        const fresh = notes.filter((n) => store.fresh(n.key));
        fresh.forEach((n) => store.mark(n.key));
        if (fresh.length) {
          process.stdout.write(JSON.stringify({
            hookSpecificOutput: {
              hookEventName: 'PostToolUse',
              additionalContext:
                'BLAST RADIUS — this edit matches a symptom-patch fingerprint:\n\n'
                + fresh.map((n) => n.text).join('\n\n'),
            },
          }));
        }
      }
    } catch {
      // fail open — a guard must never break an edit
    }
    process.exit(0);
  });
}
