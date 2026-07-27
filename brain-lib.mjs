// Shared read/score/write layer for the brain hooks (brain-gate, brain-harvest, brain-capture).
// node's bundled SQLite has no FTS5, so every lookup here is a LIKE candidate scan scored in JS —
// the same approach prompt-recall.mjs proved out, generalised over all five channels.
import { existsSync, readFileSync, writeFileSync, mkdirSync } from 'node:fs';
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

export const dbPathFor = (instance) => join(homedir(), '.aitm', instance, 'aitm.db');

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
    sql: `SELECT term AS head, substr(value,1,600) AS body, 0 AS hard, 0 AS hits FROM facts WHERE {W}`,
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
          COALESCE(u.hits,0) AS hits
          FROM node_now n LEFT JOIN usage u ON u.node_k = n.k WHERE {W}`,
    cols: ['n.label', 'n.gloss', 'n.k'],
    weight: 0.9,
    headMax: 90,
  },
  {
    kind: 'doc',
    sql: `SELECT title AS head, substr(content,1,600) AS body, 0 AS hard, 0 AS hits, path AS loc
          FROM docs WHERE {W}`,
    cols: ['title', 'content'],
    weight: 1.0,
    headMax: Infinity,
  },
  {
    kind: 'code',
    sql: `SELECT symbol AS head, usage AS body, 0 AS hard, 0 AS hits,
          (file || ':' || COALESCE(line,0)) AS loc FROM edges WHERE {W}`,
    cols: ['symbol', 'file', 'usage'],
    weight: 1.8,
  },
  // Transcript text is the noisiest channel and is only ever a tie-breaker, never a cited answer.
  {
    kind: 'chat',
    sql: `SELECT '' AS head, substr(text,1,600) AS body, 0 AS hard, 0 AS hits FROM chat WHERE {W}`,
    cols: ['text'],
    weight: 0.5,
  },
];

export function search(db, tokens, { limit = 5, exclude = [] } = {}) {
  if (tokens.length === 0) return [];
  const rows = [];
  for (const ch of CHANNELS) {
    if (exclude.includes(ch.kind)) continue;
    const where = tokens.map(() => '(' + ch.cols.map((c) => `${c} LIKE ?`).join(' OR ') + ')').join(' OR ');
    const params = tokens.flatMap((t) => Array(ch.cols.length).fill(`%${t}%`));
    try {
      for (const r of db.prepare(ch.sql.replace('{W}', where)).all(...params)) {
        rows.push({ ...r, kindTag: ch.kind, weight: ch.weight, headMax: ch.headMax });
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
    const blobPenalty = (body.length > 600 ? 1.5 : 0) + (head.length === 0 ? 2 : 0);
    const base = headCover * 3 + bodyCover + Math.log2((r.hits || 0) + 1) * 0.5 + (r.hard ? 2 : 0) - blobPenalty;
    return { ...r, head, body, cover: headCover + bodyCover, score: base * r.weight };
  }).filter((r) => r.cover >= minCover && r.score > 0);

  scored.sort((x, y) => y.score - x.score);

  const seen = new Set();
  const picks = [];
  for (const r of scored) {
    const key = (r.head || r.body).toLowerCase().slice(0, 80);
    if (seen.has(key)) continue;
    seen.add(key);
    picks.push(r);
    if (picks.length === limit) break;
  }
  return picks;
}

export function format(picks) {
  return picks.map((r) => {
    const tag = `[${r.kindTag}${r.hard ? '/HARD' : ''}]`;
    const loc = r.loc ? `  (${r.loc})` : '';
    return r.head.length > 0
      ? `• ${tag} ${clip(r.head, 90)}${loc}\n    ${clip(r.body, 260)}`
      : `• ${tag} ${clip(r.body, 220)}${loc}`;
  }).join('\n');
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
  return join(homedir(), '.aitm', instance, 'gate', `${String(sessionId || 'nosession').slice(0, 64)}.json`);
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
