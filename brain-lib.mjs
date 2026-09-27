// Shared read/score/write layer for the brain hooks (brain-gate, brain-harvest, brain-capture).
// node's bundled SQLite has no FTS5, so every lookup here is a LIKE candidate scan scored in JS —
// the same approach prompt-recall.mjs proved out, generalised over all five channels.
import { existsSync, readFileSync, writeFileSync, mkdirSync, statSync, openSync, readSync, closeSync } from 'node:fs';
import { homedir } from 'node:os';
import { join, basename, dirname } from 'node:path';

const STOP = new Set([
  'the', 'is', 'a', 'an', 'of', 'to', 'in', 'on', 'for', 'and', 'or', 'what', 'how', 'are', 'does',
  'do', 'it', 'its', 'be', 'this', 'that', 'with', 'as', 'at', 'by', 'my', 'i', 'you', 'we', 'there',
  'was', 'were', 'which', 'when', 'where', 'name', 'called', 'get', 'got', 'me', 'us', 'about',
  'can', 'please', 'lets', 'let', 'make', 'need', 'want', 'should', 'would', 'could', 'will', 'just',
  'now', 'then', 'fix', 'add', 'use', 'run', 'check', 'look', 'help', 'why', 'not', 'but', 'if', 'so',
  'new', 'all', 'any', 'from', 'into', 'out', 'up', 'down', 'has', 'have', 'had', 'been', 'null',
]);

export const clip = (s, n) => (s.length <= n ? s : s.slice(0, n).trimEnd() + '…');

export function resolveInstance(payload = {}) {
  const proj = process.env.CLAUDE_PROJECT_DIR || payload.cwd || process.cwd();
  return basename(proj.replace(/[\\/]+$/, '')).toLowerCase().replace(/[^a-z0-9_-]/g, '');
}

export const dbPathFor = (instance) => join(homedir(), '.grimora', instance, 'grimora.db');

export async function openRead(instance) {
  const path = dbPathFor(instance);
  if (!existsSync(path)) return null;
  const { DatabaseSync } = await import('node:sqlite');
  const db = new DatabaseSync(path, { readOnly: true });
  db.exec('PRAGMA busy_timeout = 4000');
  return db;
}

export async function openWrite(instance) {
  const path = dbPathFor(instance);
  if (!existsSync(path)) return null;
  const { DatabaseSync } = await import('node:sqlite');
  const db = new DatabaseSync(path);
  db.exec('PRAGMA busy_timeout = 4000');
  return db;
}

// A regex is not a sentence: strip the metacharacters before tokenising, and split identifiers so
// `TvCarouselAdapter` and `resolve_video` both reach the store as their component words.
export function tokenize(raw, { splitIdentifiers = true } = {}) {
  const cleaned = String(raw || '')
    .replace(/\\[a-zA-Z]/g, ' ')
    .replace(/[(){}[\]|^$*+?.\\/'"`<>=!,;:#@&%~-]/g, ' ');
  const words = [];
  for (const chunk of cleaned.split(/\s+/)) {
    if (!chunk) continue;
    words.push(chunk);
    if (splitIdentifiers) {
      for (const part of chunk.split(/_|(?<=[a-z0-9])(?=[A-Z])/)) {
        if (part && part !== chunk) words.push(part);
      }
    }
  }
  return [...new Set(words.map((w) => w.toLowerCase()).filter((w) => w.length > 2 && !STOP.has(w)))].slice(0, 8);
}

export const signature = (tokens) => [...tokens].sort().join(' ');

// headMax guards against a channel whose head field is a dumped session log. It applies to node
// only: a memory hook and a doc title are full sentences by design, and demoting those buried the
// single best-matching field while the log blobs survived the penalty.
const CHANNELS = [
  {
    kind: 'fact',
    sql: `SELECT term AS head, substr(value,1,600) AS body, 0 AS hard, 0 AS hits,
          COALESCE(provenance,'unverified') AS provenance FROM facts WHERE {W}`,
    cols: ['term', 'value', 'aliases'],
    weight: 1.6,
    headMax: Infinity,
  },
  {
    kind: 'rule',
    sql: `SELECT hook AS head, substr(body,1,600) AS body, hard AS hard, 0 AS hits FROM memory WHERE {W}`,
    cols: ['hook', 'title', 'body'],
    weight: 1.5,
    headMax: Infinity,
  },
  {
    kind: 'node',
    sql: `SELECT substr(n.label,1,300) AS head, substr(n.gloss,1,600) AS body, n.hard AS hard,
          COALESCE(u.hits,0) AS hits, COALESCE(n.scheme,'') AS scheme
          FROM node_now n LEFT JOIN usage u ON u.node_k = n.k WHERE {W}`,
    cols: ['n.label', 'n.gloss', 'n.k'],
    weight: 0.9,
    headMax: 90,
  },
  {
    kind: 'doc',
    sql: `SELECT title AS head, substr(content,1,600) AS body, 0 AS hard, 0 AS hits, path AS loc
          FROM docs WHERE {W}`,
    // terms holds the folder and file names. A document's own name is usually in its path rather than
    // its prose, so without this a search for the thing by name misses the sections that describe it.
    cols: ['title', 'content', 'terms'],
    table: 'docs',
    weight: 1.0,
    headMax: Infinity,
  },
  {
    kind: 'code',
    sql: `SELECT symbol AS head, usage AS body, 0 AS hard, 0 AS hits,
          (file || ':' || COALESCE(line,0)) AS loc, file AS srcfile FROM edges WHERE {W}`,
    cols: ['symbol', 'file', 'usage'],
    weight: 1.8,
  },
  // The back-and-forth is the point of the store, not noise around it: a decision argued four months
  // ago still governs a change made today, and this is the only channel that carries it. It is
  // structurally headless, so it is exempt from the missing-head penalty rather than buried by it;
  // maxPerKind keeps it supporting the answer instead of flooding it.
  {
    kind: 'chat',
    sql: `SELECT '' AS head, substr(text,1,600) AS body, 0 AS hard, 0 AS hits, ts AS loc FROM chat WHERE {W}`,
    cols: ['text'],
    weight: 0.8,
    headless: true,
    maxPerKind: 2,
  },
];

// SQL LIKE matches anywhere in the string, which is fine for prose and wrong for a 29k-symbol code
// index: the prompt word "stand" hits TrackerStandsDown and outranks the real answer. A symbol only
// counts when a token equals the whole name or one of its case/underscore-separated parts.
function symbolMatches(symbol, tokens) {
  const name = String(symbol || '').toLowerCase();
  if (!name) return false;
  const parts = new Set([name, ...String(symbol).split(/_|(?<=[a-z0-9])(?=[A-Z])/).map((p) => p.toLowerCase())]);
  return tokens.some((t) => parts.has(t));
}

// A store written by an older CLI is missing columns a newer one queries. Naming a column that does
// not exist throws, and the per-channel catch below would then drop that whole channel in silence —
// a worse failure than the missing column, because the store looks empty rather than out of date.
export function columnsPresent(db, table, wanted) {
  try {
    const have = new Set(db.prepare(`SELECT name FROM pragma_table_info('${table}')`).all().map((r) => r.name));
    const kept = wanted.filter((c) => have.has(c.split('.').pop()));
    return kept.length > 0 ? kept : wanted;
  } catch { return wanted; }
}

export function search(db, tokens, { limit = 5, exclude = [] } = {}) {
  if (tokens.length === 0) return [];
  const rows = [];
  for (const ch of CHANNELS) {
    if (exclude.includes(ch.kind)) continue;
    const cols = ch.table ? columnsPresent(db, ch.table, ch.cols) : ch.cols;
    const where = tokens.map(() => '(' + cols.map((c) => `${c} LIKE ?`).join(' OR ') + ')').join(' OR ');
    const params = tokens.flatMap((t) => Array(cols.length).fill(`%${t}%`));
    try {
      for (const r of db.prepare(ch.sql.replace('{W}', where)).all(...params)) {
        rows.push({ ...r, kindTag: ch.kind, weight: ch.weight, headMax: ch.headMax, headless: ch.headless, maxPerKind: ch.maxPerKind });
      }
    } catch { /* channel absent in this store */ }
  }

  const minCover = tokens.length >= 3 ? 2 : 1;
  const scored = rows.map((r) => {
    // A paragraph-length head is a log dump that matches every common token; it gets no head
    // privileges at all, or session-log rows outrank the actual answer on every broad question.
    let head = r.head || '';
    let body = r.body || '';
    if (head.length > (r.headMax ?? 90)) { body = `${head} ${body}`; head = ''; }
    const headHay = head.toLowerCase();
    const bodyHay = body.slice(0, 500).toLowerCase();
    const headCover = tokens.filter((t) => headHay.includes(t)).length;
    const bodyCover = tokens.filter((t) => !headHay.includes(t) && bodyHay.includes(t)).length;
    const blobPenalty = (body.length > 600 ? 1.5 : 0) + (head.length === 0 && !r.headless ? 2 : 0);
    // A repo or org node is a deliberately curated identity: it is what "what is X" is asking for, and
    // it must outrank a symbol some extractor happened to find in a test file.
    const IDENTITY_SCHEMES = new Set(['repo', 'org', 'host', 'service', 'registry', 'domain']);
    const identity = r.kindTag === 'node' && IDENTITY_SCHEMES.has(r.scheme) ? 3 : 0;
    // A declaration inside a test is evidence the thing exists, not an explanation of what it is.
    const testPenalty = r.kindTag === 'code' && /(^|[\\/])(tests?|spec|__tests__)[\\/]/i.test(r.srcfile || '') ? 2.5 : 0;
    const base = headCover * 3 + bodyCover + Math.log2((r.hits || 0) + 1) * 0.5 + (r.hard ? 2 : 0)
      + identity - blobPenalty - testPenalty;
    return { ...r, head, body, cover: headCover + bodyCover, score: base * r.weight };
  }).filter((r) => r.cover >= minCover && r.score > 0)
    .filter((r) => r.kindTag !== 'code' || symbolMatches(r.head, tokens));

  scored.sort((x, y) => y.score - x.score);

  const seen = new Set();
  const perKind = new Map();
  const picks = [];
  for (const r of scored) {
    const key = (r.head || r.body).toLowerCase().slice(0, 80);
    if (seen.has(key)) continue;
    const used = perKind.get(r.kindTag) || 0;
    if (r.maxPerKind && used >= r.maxPerKind) continue;
    seen.add(key);
    perKind.set(r.kindTag, used + 1);
    picks.push(r);
    if (picks.length === limit) break;
  }
  return picks;
}

// A transcript row that is just a context-continuation preamble describes the session, not the code.
const TRANSCRIPT_BOILERPLATE = /^(this session is being continued|<\?xml|summary:|caveat: the messages below)/i;

// The track record for one file: what has been decided about it, not where it lives. Two signals are
// combined — a literal match on the path tail, which is high precision because rules cite real paths,
// and the symbols the file declares, which catches decisions that named the symbol but not the file.
export function historyFor(db, filePath, { limit = 4 } = {}) {
  const norm = String(filePath).replace(/\\/g, '/');
  const base = norm.split('/').pop() || '';
  const tail = norm.split('/').slice(-2).join('/');

  // Decisions cite the class, not the filename: the smart-copy rule names PlanStage.ApplySmartCopy‐
  // Downgrade and never "PlanStage.cs". So the bare stem is matched too, but only when it is
  // distinctive — a compound identifier or a long one. "Desktop" as a stem matches every rule that
  // mentions a desktop; "PlanStage" and "VideoPlaylistItem" pick out the code they belong to.
  const stem = base.replace(/\.[^.]+$/, '');
  const humps = (stem.match(/[A-Z]/g) || []).length;
  const needles = [tail, base];
  if (stem.length >= 10 || humps >= 2) needles.push(stem);

  const literal = [];
  for (const ch of [
    { kind: 'rule', sql: `SELECT hook AS head, substr(body,1,600) AS body, hard FROM memory WHERE body LIKE ? OR hook LIKE ?` },
    { kind: 'fact', sql: `SELECT term AS head, substr(value,1,600) AS body, 0 AS hard FROM facts WHERE value LIKE ?  OR term LIKE ?` },
    { kind: 'doc', sql: `SELECT title AS head, substr(content,1,600) AS body, 0 AS hard FROM docs WHERE content LIKE ? OR (title || ' ' || COALESCE(terms,'')) LIKE ?` },
    { kind: 'chat', sql: `SELECT '' AS head, substr(text,1,600) AS body, 0 AS hard FROM chat WHERE text LIKE ?  OR text LIKE ?` },
  ]) {
    for (const needle of needles) {
      try {
        for (const r of db.prepare(ch.sql).all(`%${needle}%`, `%${needle}%`)) {
          literal.push({ ...r, kindTag: ch.kind, score: 100, headless: ch.kind === 'chat' });
        }
      } catch { /* channel absent */ }
    }
  }

  // Only the symbols the file actually declares. Path segments were tried and are far too generic:
  // "views/Base/Watch/Desktop.vue" pulled in every rule mentioning watch or desktop anywhere.
  let symbolic = [];
  try {
    // Windows hands back whatever casing the caller used, and the index stores whatever the walk saw.
    // Matching those raw made the track record silently empty for any file whose drive letter differed.
    const tokens = db.prepare('SELECT DISTINCT symbol FROM edges WHERE LOWER(file) = LOWER(?) LIMIT 12').all(norm)
      .map((r) => String(r.symbol).toLowerCase())
      .filter((s) => s.length >= 5)
      .slice(0, 6);
    // A single generic symbol ("desktop") matches half the store, and search() drops to minCover 1
    // for one token. Two or more is the point where the combination actually identifies this file.
    if (tokens.length >= 2) symbolic = search(db, tokens, { limit: limit * 2, exclude: ['code'] });
  } catch { /* edges absent */ }

  const seen = new Set();
  const picks = [];
  for (const r of [...literal, ...symbolic]) {
    const body = String(r.body || '').trim();
    if (TRANSCRIPT_BOILERPLATE.test(body)) continue;
    const key = (r.head || body).toLowerCase().slice(0, 80);
    if (!key || seen.has(key)) continue;
    seen.add(key);
    picks.push(r);
    if (picks.length === limit) break;
  }
  return picks;
}

// Rows that are already as good as the file they came from. Re-opening the source to confirm one of
// these is pure cost: a hard rule and a stated fact are the operator's own words, and an extracted row
// names the file it was read out of, so the check has already been done and recorded.
const TRUSTED = new Set(['stated', 'extracted']);

export function format(picks, { caveat = false } = {}) {
  return picks.map((r) => {
    // "stated" carries the operator's authority; "inferred" is the agent's own conclusion and can be
    // overturned by evidence. Collapsing the two is how a guess starts getting cited as ground truth.
    const prov = r.provenance && r.provenance !== 'unverified' ? `/${r.provenance}` : '';
    const tag = `[${r.kindTag}${r.hard ? '/HARD' : ''}${prov}]`;
    const loc = r.loc ? `  (${r.loc})` : '';
    // Marking only the rows that need it, rather than warning about all of them, is the difference
    // between a caveat that gets read and one that gets applied to the answer as well as the guess.
    const doubt = caveat && !r.hard && !TRUSTED.has(r.provenance) ? '  ← unconfirmed, check before acting' : '';
    return r.head.length > 0
      ? `• ${tag} ${clip(r.head, 90)}${loc}${doubt}\n    ${clip(r.body, 260)}`
      : `• ${tag} ${clip(r.body, 220)}${loc}${doubt}`;
  }).join('\n');
}

// Label-propagation communities over the triple graph. Cheap, deterministic given a fixed iteration
// order, and needs no tuning — which matters because this runs on demand, not on a schedule.
//
// Computed when asked rather than injected every session: a standing summary costs tokens on every
// turn whether or not the work touches it, and the whole point of the store is to spend context only
// where a change actually reaches.
export function communities(db, { rounds = 8 } = {}) {
  let rows = [];
  try {
    rows = db.prepare(
      `SELECT s, p, o FROM triple WHERE valid_to IS NULL AND o_is_literal = 0`
    ).all();
  } catch {
    return [];
  }
  if (rows.length === 0) return [];

  // Edges are weighted by how rare their predicate is, for the same reason text ranking uses IDF: a
  // predicate carried by most of the graph says nothing about who belongs with whom. Unweighted, the
  // catch-all "related" (29% of links) welded two thirds of the store into one meaningless cluster.
  const freq = new Map();
  for (const r of rows) freq.set(r.p, (freq.get(r.p) || 0) + 1);
  const weightOf = (p) => 1 / (1 + Math.log(1 + (freq.get(p) || 1)));

  const adj = new Map();
  const link = (a, b, p) => {
    if (!adj.has(a)) adj.set(a, []);
    adj.get(a).push({ other: b, w: weightOf(p) });
  };
  for (const r of rows) { link(r.s, r.o, r.p); link(r.o, r.s, r.p); }

  const label = new Map();
  for (const k of adj.keys()) label.set(k, k);
  const keys = [...adj.keys()].sort();

  for (let i = 0; i < rounds; i++) {
    let moved = 0;
    for (const k of keys) {
      const tally = new Map();
      for (const edge of adj.get(k)) {
        const nl = label.get(edge.other);
        tally.set(nl, (tally.get(nl) || 0) + edge.w);
      }
      if (tally.size === 0) continue;
      // Ties break on the lexically smaller label so the result is stable across runs.
      const best = [...tally.entries()].sort((a, b) => b[1] - a[1] || (a[0] < b[0] ? -1 : 1))[0][0];
      if (best !== label.get(k)) { label.set(k, best); moved++; }
    }
    if (moved === 0) break;
  }

  const groups = new Map();
  for (const [node, lab] of label) {
    if (!groups.has(lab)) groups.set(lab, []);
    groups.get(lab).push(node);
  }

  const named = new Map();
  try {
    for (const r of db.prepare('SELECT k, label, kind FROM node WHERE valid_to IS NULL').all()) named.set(r.k, r);
  } catch { /* node table absent */ }

  return [...groups.values()]
    .filter((members) => members.length > 1)
    .map((members) => {
      const sorted = members.slice().sort((a, b) => (adj.get(b)?.length || 0) - (adj.get(a)?.length || 0));
      const hub = sorted[0];
      return {
        // The most connected member is the god node: the thing everything else in the cluster hangs off.
        hub: named.get(hub)?.label || hub,
        hubKey: hub,
        size: members.length,
        members: sorted.map((m) => ({ key: m, label: named.get(m)?.label || m, kind: named.get(m)?.kind || '' })),
      };
    })
    .sort((a, b) => b.size - a.size);
}

// The cluster a given node belongs to — used to answer "what else is bound up with this decision".
export function communityOf(db, nodeKey, opts) {
  return communities(db, opts).find((c) => c.members.some((m) => m.key === nodeKey)) || null;
}

// Resolve a loose name to a live node key: exact key, then exact label, then a contained label.
export function resolveNode(db, name) {
  const q = String(name || '').trim();
  if (!q) return null;
  for (const [sql, param] of [
    ['SELECT k, label FROM node WHERE valid_to IS NULL AND k = ? LIMIT 1', q],
    ['SELECT k, label FROM node WHERE valid_to IS NULL AND LOWER(label) = LOWER(?) LIMIT 1', q],
    ['SELECT k, label FROM node WHERE valid_to IS NULL AND label LIKE ? ORDER BY length(label) LIMIT 1', `%${q}%`],
  ]) {
    try {
      const r = db.prepare(sql).get(param);
      if (r) return r;
    } catch { /* node table absent */ }
  }
  return null;
}

// Shortest connection between two nodes, walked in both directions because "what does A have to do
// with B" does not care which way the relationship was authored. This is the query behind "what
// breaks if I change this": the chain, with the reason each link was recorded.
export function pathBetween(db, fromName, toName, { maxDepth = 5 } = {}) {
  const a = resolveNode(db, fromName);
  const b = resolveNode(db, toName);
  if (!a || !b) return { from: a, to: b, hops: null };
  if (a.k === b.k) return { from: a, to: b, hops: [] };

  let neighbours;
  try {
    neighbours = db.prepare(
      `SELECT p, o AS other, because, 'out' AS dir FROM triple WHERE valid_to IS NULL AND o_is_literal = 0 AND s = ?
       UNION ALL
       SELECT p, s AS other, because, 'in'  AS dir FROM triple WHERE valid_to IS NULL AND o_is_literal = 0 AND o = ?`
    );
  } catch {
    return { from: a, to: b, hops: null };
  }

  const seen = new Set([a.k]);
  let frontier = [{ k: a.k, hops: [] }];
  for (let depth = 0; depth < maxDepth; depth++) {
    const next = [];
    for (const cur of frontier) {
      let rows = [];
      try { rows = neighbours.all(cur.k, cur.k); } catch { /* skip */ }
      for (const r of rows) {
        if (seen.has(r.other)) continue;
        seen.add(r.other);
        const hops = [...cur.hops, { from: cur.k, pred: r.p, to: r.other, dir: r.dir, because: r.because || '' }];
        if (r.other === b.k) return { from: a, to: b, hops };
        next.push({ k: r.other, hops });
      }
    }
    if (next.length === 0) break;
    frontier = next;
  }
  return { from: a, to: b, hops: null };
}

// Blast radius for a file: the symbols it declares that also appear elsewhere. A symbol carried by
// more than one project is a contract surface, and changing it is how one fix silently breaks
// something else — the exact failure this store exists to prevent.
export function blastRadius(db, filePath, { limit = 5 } = {}) {
  const norm = String(filePath).replace(/\\/g, '/');
  try {
    // Counted across every row for the symbol, not just the rows in other files. Joining against
    // "other files only" made a symbol shared with exactly one other project count as one project,
    // so the two-project case — the commonest contract surface there is — was silently dropped.
    return db.prepare(
      `SELECT e.symbol,
              (SELECT COUNT(DISTINCT a.file) FROM edges a WHERE a.symbol = e.symbol) AS files,
              (SELECT COUNT(DISTINCT a.project) FROM edges a WHERE a.symbol = e.symbol AND a.project IS NOT NULL) AS projects,
              (SELECT GROUP_CONCAT(DISTINCT a.project) FROM edges a WHERE a.symbol = e.symbol AND a.project IS NOT NULL) AS names
       FROM edges e
       WHERE LOWER(e.file) = LOWER(?)
       GROUP BY e.symbol
       HAVING projects > 1 OR files > 1
       ORDER BY projects DESC, files DESC
       LIMIT ?`
    ).all(norm, limit);
  } catch {
    return [];
  }
}

const nowIso = () => new Date().toISOString();

export function logGap(db, tool, query) {
  try {
    db.prepare(
      `INSERT INTO gaps(query, tool, misses, first_ts, last_ts, status) VALUES(?, ?, 1, ?, ?, 'open')
       ON CONFLICT(query) DO UPDATE SET misses = misses + 1, last_ts = excluded.last_ts`
    ).run(query, tool, nowIso(), nowIso());
  } catch { /* never let bookkeeping break a tool call */ }
}

// Session-scoped gate ledger. The gate must never deadlock: a signature it has already answered for
// is allowed straight through on the next attempt, so a genuine miss always reaches the filesystem.
export function ledgerPath(instance, sessionId) {
  return join(homedir(), '.grimora', instance, 'gate', `${String(sessionId || 'nosession').slice(0, 64)}.json`);
}

// Shared by the PreCompact writer and the UserPromptSubmit reader. It lives here rather than in either
// hook because importing a hook module RUNS it: compact-restore imported this one name from
// compact-brief and thereby fired the whole brief hook on every prompt, printing its output too.
export function briefPath(instance, sessionId) {
  return join(homedir(), '.grimora', instance, 'compact', `${String(sessionId || 'x').slice(0, 64)}.md`);
}

// Read the END of a transcript rather than all of it.
//
// Every Stop and UserPromptSubmit hook needs the last turn, and each was reading and JSON-parsing the
// whole file to get it: 735ms on a 49 MB session, on every single prompt, times four hooks. A turn is
// kilobytes. The first line in the window is dropped because a byte offset lands mid-record.
//
// wantFull is the escape hatch: if the caller cannot find what it needs in the tail (a turn longer than
// the window), it says so and gets the whole file, so this is a speed change and never a correctness one.
export function tailEntries(path, { maxBytes = 4 * 1024 * 1024 } = {}) {
  let text, truncated = false;
  const size = statSync(path).size;
  if (size <= maxBytes) {
    text = readFileSync(path, 'utf8');
  } else {
    const fd = openSync(path, 'r');
    try {
      const buf = Buffer.allocUnsafe(maxBytes);
      readSync(fd, buf, 0, maxBytes, size - maxBytes);
      text = buf.toString('utf8');
    } finally { closeSync(fd); }
    text = text.slice(text.indexOf('\n') + 1);
    truncated = true;
  }
  const entries = [];
  for (const line of text.split('\n')) {
    if (!line.trim()) continue;
    try { entries.push(JSON.parse(line)); } catch { /* partial or split record */ }
  }
  return { entries, truncated };
}

export function readLedger(path) {
  try { return JSON.parse(readFileSync(path, 'utf8')); } catch { return { seen: {}, allowed: [] }; }
}

export function writeLedger(path, data) {
  try {
    mkdirSync(dirname(path), { recursive: true });
    writeFileSync(path, JSON.stringify(data));
  } catch { /* ledger is best-effort */ }
}
