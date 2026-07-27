// Ranking regression tests. The store is load-bearing for every session now, and a scoring change
// does not fail loudly — it just quietly answers with worse material, which is indistinguishable from
// the model being dim. Each case below is a bug that actually shipped and cost real time.
//
// Runs against a throwaway store built in a temp directory, never the live one.
//
//   node brain-lib.test.mjs
import { mkdtempSync, rmSync, mkdirSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { DatabaseSync } from 'node:sqlite';
import { search, tokenize, historyFor, blastRadius, signature } from './brain-lib.mjs';

let passed = 0;
const failures = [];
function check(name, condition, detail = '') {
  if (condition) { passed++; console.log(`  ok   ${name}`); }
  else { failures.push(`${name}${detail ? ` — ${detail}` : ''}`); console.log(`  FAIL ${name}${detail ? ` — ${detail}` : ''}`); }
}

const dir = mkdtempSync(join(tmpdir(), 'aitm-test-'));
mkdirSync(dir, { recursive: true });
const db = new DatabaseSync(join(dir, 'aitm.db'));

db.exec(`
  CREATE TABLE facts(k TEXT PRIMARY KEY, term TEXT, aliases TEXT, category TEXT, value TEXT, source TEXT, notes TEXT, provenance TEXT DEFAULT 'unverified');
  CREATE TABLE memory(k TEXT PRIMARY KEY, type TEXT, title TEXT, hook TEXT, body TEXT, links TEXT, hard INTEGER);
  CREATE TABLE node_now(id INTEGER, k TEXT, kind TEXT, label TEXT, gloss TEXT, scheme TEXT, props TEXT, hard INTEGER, valid_from TEXT, valid_to TEXT, superseded_by INTEGER);
  CREATE TABLE usage(node_k TEXT, hits INTEGER, last_used TEXT, verified_at TEXT);
  CREATE TABLE docs(k TEXT PRIMARY KEY, path TEXT, title TEXT, category TEXT, content TEXT);
  CREATE TABLE edges(id INTEGER PRIMARY KEY, symbol TEXT, contract TEXT, project TEXT, file TEXT, line INTEGER, usage TEXT, hardcoded INTEGER);
  CREATE TABLE chat(k TEXT PRIMARY KEY, session TEXT, ts TEXT, role TEXT, text TEXT);
`);

// 1. Unranked LIMIT truncation. A broad token matches many rows; the exact answer must still win.
const insertRule = db.prepare('INSERT INTO memory(k,type,title,hook,body,hard) VALUES(?,?,?,?,?,0)');
for (let i = 0; i < 120; i++) {
  insertRule.run(`noise-${i}`, 'reference', `noise ${i}`, `something about the player number ${i}`, 'filler body text');
}
// Deliberately longer than the 90-character blob threshold: a memory hook is a full sentence by
// design, and demoting it was what buried the best-matching field under session-log noise.
insertRule.run(
  'teleport', 'reference', 'teleport rule',
  'Any app-web overlay meant to appear OVER the fullscreen video player must Teleport to body as well, or z-index cannot reach it',
  'the player is body-level at z-1199 so overlays must teleport too'
);

const tokens = tokenize('teleport overlay fullscreen player');
const picks = search(db, tokens, { limit: 5 });
check('exact match survives a broad token matching 120 rows',
  picks.length > 0 && /teleport/i.test(picks[0].head),
  picks.length ? `got "${String(picks[0].head).slice(0, 50)}"` : 'no picks');

// 2. A memory hook is a full sentence by design and must not be demoted as a log blob.
check('long memory hook keeps its head', picks.length > 0 && picks[0].head.length > 90);

// 3. Chat is a first-class channel, not a penalised tie-breaker.
db.prepare('INSERT INTO chat(k,session,ts,role,text) VALUES(?,?,?,?,?)')
  .run('c1', 's', '2026-01-01', 'user', 'we decided the encoder must stream copy when the codec already matches the target');
const chatPicks = search(db, tokenize('encoder stream copy codec matches target'), { limit: 5 });
check('conversation history can place in results', chatPicks.some((p) => p.kindTag === 'chat'));

// 4. A code symbol must match on a word boundary, not a substring.
db.prepare('INSERT INTO edges(symbol,contract,project,file,line,usage,hardcoded) VALUES(?,?,?,?,?,?,0)')
  .run('aChromeCanTakeOverAndTheTrackerStandsDown', 'decl', 'p', '/x/a.kt', 1, 'kotlin declaration');
const standPicks = search(db, tokenize('what does aitm stand for'), { limit: 5 });
check('substring token does not match a symbol', !standPicks.some((p) => p.kindTag === 'code'));

db.prepare('INSERT INTO edges(symbol,contract,project,file,line,usage,hardcoded) VALUES(?,?,?,?,?,?,0)')
  .run('VideoPlaylistItem', 'decl', 'video-player', '/x/types.ts', 65, 'ts declaration');
const symPicks = search(db, tokenize('VideoPlaylistItem'), { limit: 5 });
check('whole-symbol token does match', symPicks.some((p) => p.kindTag === 'code'));

// 5. Track record: decisions cite the class name, never the filename with its extension.
db.prepare('INSERT INTO memory(k,type,title,hook,body,hard) VALUES(?,?,?,?,?,0)')
  .run('smartcopy', 'reference', 'smart copy', 'Encoder must stream-copy when the source matches the target',
       'wired via PlanStage.ApplySmartCopyDowngrade into StreamActionResolver');
const hist = historyFor(db, 'c:/repo/src/Pipeline/Stages/PlanStage.cs', { limit: 4 });
check('history matches a decision citing the class name', hist.some((h) => /stream-copy/i.test(h.head || h.body)));

// A single generic stem must not drag in everything that mentions the word.
db.prepare('INSERT INTO memory(k,type,title,hook,body,hard) VALUES(?,?,?,?,?,0)')
  .run('desktopfiles', 'feedback', 'desktop', 'Never write files to the owner\'s Desktop', 'unrelated to any view');
const deskHist = historyFor(db, 'c:/repo/src/views/Watch/Desktop.vue', { limit: 4 });
check('generic stem does not pull in unrelated matches', !deskHist.some((h) => /never write files/i.test(h.head || '')));

// 6. Blast radius spots a symbol carried by more than one project.
const ins = db.prepare('INSERT INTO edges(symbol,contract,project,file,line,usage,hardcoded) VALUES(?,?,?,?,?,?,0)');
ins.run('AudioTrackState', 'decl', 'video-player', '/repo/video/types.ts', 10, 'ts declaration');
ins.run('AudioTrackState', 'decl', 'music-player', '/repo/music/types.ts', 12, 'ts declaration');
ins.run('LocalOnly', 'decl', 'video-player', '/repo/video/types.ts', 20, 'ts declaration');
const blast = blastRadius(db, '/repo/video/types.ts', { limit: 5 });
check('cross-project symbol is flagged', blast.some((b) => b.symbol === 'AudioTrackState' && b.projects > 1));
check('single-project symbol is not flagged', !blast.some((b) => b.symbol === 'LocalOnly'));

// 7. Signatures are order-independent, so the gate ledger cannot be fooled by reordering.
check('signature is order independent', signature(tokenize('alpha beta')) === signature(tokenize('beta alpha')));

db.close();
rmSync(dir, { recursive: true, force: true });

console.log(`\n${passed} passed, ${failures.length} failed`);
if (failures.length > 0) process.exit(1);
