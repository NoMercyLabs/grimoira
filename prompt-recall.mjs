// UserPromptSubmit hook: the brain consults itself on every prompt, no tool call needed. Tokenizes
// the prompt, candidate-matches nodes/rules/facts via LIKE (node's bundled SQLite has no FTS5), scores
// in JS (term coverage + usage hits + hard flag), and injects the top hits as compact context. Strict
// noise gates: multi-token coverage required, tiny output, silent on miss, always fails open.
import { existsSync } from 'node:fs';
import { homedir } from 'node:os';
import { join, basename } from 'node:path';

const STOP = new Set([
  'the', 'is', 'a', 'an', 'of', 'to', 'in', 'on', 'for', 'and', 'or', 'what', 'how', 'are', 'does',
  'do', 'it', 'its', 'be', 'this', 'that', 'with', 'as', 'at', 'by', 'my', 'i', 'you', 'we', 'there',
  'was', 'were', 'which', 'when', 'where', 'name', 'called', 'get', 'got', 'me', 'us', 'about',
  'can', 'please', 'lets', 'let', 'make', 'need', 'want', 'should', 'would', 'could', 'will', 'just',
  'now', 'then', 'fix', 'add', 'use', 'run', 'check', 'look', 'help', 'why', 'not', 'but', 'if', 'so',
]);

const clip = (s, n) => (s.length <= n ? s : s.slice(0, n).trimEnd() + '…');

let input = '';
process.stdin.on('data', (d) => { input += d; });
process.stdin.on('end', async () => {
  try {
    const payload = JSON.parse(input || '{}');
    const prompt = (payload.prompt || '').trim();
    if (prompt.length < 12 || prompt.startsWith('/')) process.exit(0);

    const tokens = [...new Set(
      prompt.toLowerCase().split(/[^a-z0-9_-]+/)
        .filter((t) => t.length > 2 && !STOP.has(t))
    )].slice(0, 8);
    if (tokens.length < 2) process.exit(0);

    const proj = process.env.CLAUDE_PROJECT_DIR || process.cwd();
    const slug = basename(proj.replace(/[\\/]+$/, '')).toLowerCase().replace(/[^a-z0-9_-]/g, '');
    const dbPath = join(homedir(), '.aitm', slug || 'default', 'aitm.db');
    if (!existsSync(dbPath)) process.exit(0);

    const { DatabaseSync } = await import('node:sqlite');
    const db = new DatabaseSync(dbPath, { readOnly: true });
    // Store is rollback-journal; without this an MCP write window throws SQLITE_BUSY and recall
    // silently yields nothing (QC-proven: 91% loss under write burst; 0 with the timeout).
    db.exec('PRAGMA busy_timeout = 2000');
    const where = (cols) => tokens.map(() => '(' + cols.map((c) => `${c} LIKE ?`).join(' OR ') + ')').join(' OR ');
    const params = (nCols) => tokens.flatMap((t) => Array(nCols).fill(`%${t}%`));
    const rows = [];
    const collect = (sql, nCols, kind) => {
      try {
        for (const r of db.prepare(sql).all(...params(nCols))) rows.push({ ...r, kindTag: kind });
      } catch { /* table may not exist in this store */ }
    };
    collect(
      `SELECT n.label AS head, n.gloss AS body, n.hard AS hard, COALESCE(u.hits,0) AS hits
       FROM node_now n LEFT JOIN usage u ON u.node_k = n.k WHERE ${where(['n.label', 'n.gloss', 'n.k'])} LIMIT 40`,
      3, 'node'
    );
    collect(
      `SELECT hook AS head, body AS body, hard AS hard, 0 AS hits FROM memory WHERE ${where(['hook', 'title', 'body'])} LIMIT 30`,
      3, 'rule'
    );
    collect(
      `SELECT term AS head, value AS body, 0 AS hard, 0 AS hits FROM facts WHERE ${where(['term', 'value', 'aliases'])} LIMIT 30`,
      3, 'fact'
    );
    db.close();

    // Head hits dominate, body coverage only counts in the first 500 chars, and log-blob nodes
    // (very long glosses that contain every common token) are penalized — otherwise session-log
    // nodes outrank the actual rule on every broad question.
    const minCover = tokens.length >= 3 ? 2 : 1;
    const scored = rows.map((r) => {
      // A proper label/hook is a short noun phrase. A paragraph-length head is a log dump that
      // matches every common token — it gets no head privileges at all: count it as body.
      let head = r.head;
      let body = r.body || '';
      if (head.length > 90) { body = `${head} ${body}`; head = ''; }
      const headHay = head.toLowerCase();
      const bodyHay = body.slice(0, 500).toLowerCase();
      const headCover = tokens.filter((t) => headHay.includes(t)).length;
      const bodyCover = tokens.filter((t) => !headHay.includes(t) && bodyHay.includes(t)).length;
      const blobPenalty = (body.length > 600 ? 1.5 : 0) + (head.length === 0 ? 2 : 0);
      return {
        ...r,
        head,
        body,
        cover: headCover + bodyCover,
        score: headCover * 3 + bodyCover + Math.log2((r.hits || 0) + 1) * 0.5 + (r.hard ? 2 : 0) - blobPenalty,
      };
    }).filter((r) => r.cover >= minCover);
    scored.sort((x, y) => y.score - x.score);

    const seen = new Set();
    const picks = [];
    for (const r of scored) {
      const key = (r.head || r.body).toLowerCase().slice(0, 80);
      if (seen.has(key)) continue;
      seen.add(key);
      picks.push(r);
      if (picks.length === 4) break;
    }
    if (picks.length === 0) process.exit(0);

    const body = picks
      .map((r) => r.head.length > 0
        ? `• [${r.kindTag}${r.hard ? '/HARD' : ''}] ${clip(r.head, 80)} — ${clip(r.body, 140)}`
        : `• [${r.kindTag}${r.hard ? '/HARD' : ''}] ${clip(r.body, 200)}`)
      .join('\n');
    process.stdout.write(JSON.stringify({
      hookSpecificOutput: {
        hookEventName: 'UserPromptSubmit',
        additionalContext: `aitm brain recall (auto, verify via fact()/brain_recall before relying):\n${body}`,
      },
    }));
  } catch {
    // fail open — never block a prompt on a recall error
  }
  process.exit(0);
});
