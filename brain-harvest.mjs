// PostToolUse hook on Grep|Glob: the flywheel. When a search does run, whatever it found is folded
// back into the store as code edges, so the same question is answered from the brain next time
// instead of costing another scan. Coverage then grows as a side effect of normal work rather than
// depending on anyone remembering to record anything.
//
// Writes are capped and de-duplicated, and it always fails open.
import { writeFileSync, mkdirSync } from 'node:fs';
import { homedir } from 'node:os';
import { join, dirname } from 'node:path';
import { resolveInstance, openWrite, tokenize, signature } from './brain-lib.mjs';

const MAX_EDGES_PER_SEARCH = 40;
const IDENT = /^[A-Za-z_][A-Za-z0-9_]{3,}$/;
// Language keywords are identifier-shaped but are never the symbol the search is about.
const KEYWORDS = new Set([
  'class', 'interface', 'function', 'const', 'export', 'import', 'public', 'private', 'protected',
  'static', 'return', 'async', 'await', 'void', 'null', 'true', 'false', 'this', 'type', 'enum',
  'struct', 'record', 'namespace', 'using', 'package', 'suspend', 'override', 'internal', 'string',
]);

// Case is identity for a symbol: edges written lowercase never match an exact lookup or the rows
// index-code.mjs writes, so the pattern is read raw here rather than through the lowercasing tokenizer.
function symbolFrom(pattern) {
  const candidates = (String(pattern).match(/[A-Za-z_][A-Za-z0-9_]*/g) || [])
    .filter((s) => IDENT.test(s) && !KEYWORDS.has(s.toLowerCase()));
  if (candidates.length === 0) return null;
  return candidates.sort((a, b) => b.length - a.length)[0];
}

// Pull "path:line:text" / "path:line" / bare-path shapes out of whatever the tool returned.
function extractHits(response) {
  const text = typeof response === 'string'
    ? response
    : [response?.content, response?.output, response?.stdout, response?.result,
       Array.isArray(response?.filenames) ? response.filenames.join('\n') : null]
      .filter((v) => typeof v === 'string').join('\n');
  if (!text) return [];
  const hits = [];
  for (const raw of text.split(/\r?\n/)) {
    const line = raw.trim();
    if (!line) continue;
    const m = line.match(/^(?<file>(?:[A-Za-z]:)?[^:*?"<>|]+\.[A-Za-z0-9]+):(?<line>\d+):(?<txt>.*)$/)
      || line.match(/^(?<file>(?:[A-Za-z]:)?[^:*?"<>|]+\.[A-Za-z0-9]+)$/);
    if (!m?.groups?.file) continue;
    hits.push({
      file: m.groups.file.replace(/\\/g, '/'),
      line: m.groups.line ? Number(m.groups.line) : 0,
      usage: (m.groups.txt || '').trim().slice(0, 200),
    });
    if (hits.length >= MAX_EDGES_PER_SEARCH) break;
  }
  return hits;
}

function projectFor(db, file) {
  try {
    for (const p of db.prepare('SELECT name, root FROM projects').all()) {
      const root = String(p.root).replace(/\\/g, '/').toLowerCase();
      if (file.toLowerCase().startsWith(root)) return p.name;
    }
  } catch { /* projects table absent */ }
  return null;
}

let input = '';
process.stdin.on('data', (d) => { input += d; });
process.stdin.on('end', async () => {
  let db = null;
  try {
    const payload = JSON.parse(input || '{}');
    const instance = resolveInstance(payload);
    const pattern = payload.tool_input?.pattern || '';
    if (!pattern) process.exit(0);

    const hits = extractHits(payload.tool_response);
    if (process.env.AITM_HARVEST_DEBUG) {
      const p = join(homedir(), '.aitm', instance, 'harvest-last.json');
      mkdirSync(dirname(p), { recursive: true });
      writeFileSync(p, JSON.stringify({ tool: payload.tool_name, pattern, hits: hits.length, payload }, null, 2));
    }
    if (hits.length === 0) process.exit(0);

    // Only identifier-shaped patterns become edges; a prose search has no symbol to hang them on.
    const symbol = symbolFrom(pattern);
    db = await openWrite(instance);
    if (!db) process.exit(0);

    if (symbol) {
      const exists = db.prepare('SELECT 1 FROM edges WHERE symbol=? AND file=? AND line=? LIMIT 1');
      const insert = db.prepare(
        'INSERT INTO edges(symbol, contract, project, file, line, usage, hardcoded) VALUES(?,?,?,?,?,?,0)'
      );
      let added = 0;
      for (const h of hits) {
        if (exists.get(symbol, h.file, h.line)) continue;
        insert.run(symbol, null, projectFor(db, h.file), h.file, h.line, h.usage);
        added++;
      }
      if (added > 0) {
        process.stdout.write(JSON.stringify({
          suppressOutput: true,
          systemMessage: `aitm: learned ${added} location(s) for "${symbol}" from this search`,
        }));
      }
    }

    // The gate logged this search as a gap when it could not answer it; the search has now answered it.
    const sig = signature(tokenize(pattern));
    try { db.prepare("UPDATE gaps SET status='filled' WHERE query=? AND status='open'").run(sig); } catch { /* ignore */ }
  } catch {
    // fail open — harvesting must never disturb the tool result
  } finally {
    try { if (db) db.close(); } catch { /* already closed */ }
  }
  process.exit(0);
});
