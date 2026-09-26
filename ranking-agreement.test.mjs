// Cross-implementation agreement. The store is ranked twice: the CLI uses FTS5 with bm25 and a
// calibrated floor, the hooks use LIKE with JS scoring because node's bundled SQLite has no FTS5.
// Two implementations of the same judgement drift, and the drift is silent — `aitm query` and
// brain-gate would simply start answering the same question differently.
//
// This does not require identical ordering, which would be a false constraint given the hooks also
// search channels the CLI's fact lookup does not. It requires that whatever the CLI is confident
// enough to answer with is at least PRESENT in what the hook surfaces. A hook that drops the CLI's
// top answer is a regression regardless of what it found instead.
//
// Runs against a scratch instance built through the CLI, so FTS5 exists and the live store is untouched.
//
//   node ranking-agreement.test.mjs
import { spawnSync } from 'node:child_process';
import { existsSync, rmSync } from 'node:fs';
import { homedir } from 'node:os';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { openRead, tokenize, search } from './brain-lib.mjs';

const HERE = dirname(fileURLToPath(import.meta.url));
const INSTANCE = `agreement-test-${process.pid}`; // per run: parallel verify runs (worktrees) must not share a store
const STORE = join(homedir(), '.aitm', INSTANCE);

const exe = join(HERE, 'bin-cli-old', 'aitm.exe');
const dll = join(HERE, 'bin-cli-old', 'aitm.dll');
if (!existsSync(exe) && !existsSync(dll)) {
  console.log('skipped: CLI is not built (run build.ps1 first)');
  process.exit(0);
}
const cli = (...args) => {
  const useExe = existsSync(exe);
  const r = spawnSync(useExe ? exe : 'dotnet', useExe ? args : [dll, ...args], { encoding: 'utf8' });
  return `${r.stdout || ''}`.trim();
};

rmSync(STORE, { recursive: true, force: true });
cli('init', '--instance', INSTANCE);

const FACTS = [
  ['encoder stream copy rule', 'The encoder must stream-copy when the source codec and resolution already match the target profile, instead of re-encoding into the new container.'],
  ['video playlist item shape', 'VideoPlaylistItem extends BasePlaylistItem with url, image, poster, duration, subtitles, chapters and progress.'],
  ['idp realm ownership', 'The SaaS command center owns the IdP realm and accounts; the media server only validates tokens.'],
  ['overlay teleport rule', 'Any overlay that must appear over the fullscreen video player has to teleport to body, because z-index cannot cross a contain boundary.'],
  ['sqlite wal journal mode', 'The store runs in WAL journal mode so a long indexer holding the write lock never blocks readers.'],
  ['carousel focus restore', 'Carousel focus restore is identity-based so returning to a row does not reset focus to the first card.'],
];
for (const [term, value] of FACTS) {
  cli('add', '--term', term, '--value', value, '--category', 'test', '--provenance', 'stated', '--instance', INSTANCE);
}

const QUERIES = [
  'encoder stream copy codec matches target',
  'video playlist item shape subtitles chapters',
  'idp realm accounts ownership',
  'overlay teleport fullscreen player',
  'wal journal mode readers blocked',
];

const db = await openRead(INSTANCE);
let passed = 0;
const failures = [];

for (const q of QUERIES) {
  const cliOut = cli('query', q, '--instance', INSTANCE);
  const confident = !/no confident answer/i.test(cliOut);
  if (!confident) {
    // The CLI refusing is a valid answer; there is nothing for the hook to agree with.
    console.log(`  skip  CLI refused: "${q}"`);
    continue;
  }
  // The CLI prints "<bullet> <term>  [category]  (score …)" for its top hit. The bullet arrives as a
  // control byte rather than U+2022 under this console encoding, and a control byte is not whitespace,
  // so it survives trim() and quietly breaks a naive prefix strip.
  const top = (cliOut.match(/^[^\w\n]*([^[\n]+?)\s{2,}\[/m) || [])[1]?.trim();
  if (!top) { console.log(`  skip  could not parse CLI output for "${q}"`); continue; }

  const picks = search(db, tokenize(q), { limit: 5 });
  const found = picks.some((p) => String(p.head || '').toLowerCase().includes(top.toLowerCase()));
  if (found) { passed++; console.log(`  ok   both surface "${top}"`); }
  else {
    failures.push(`"${q}": CLI answered "${top}", hook surfaced [${picks.map((p) => p.head).join(' | ') || 'nothing'}]`);
    console.log(`  FAIL "${q}" — CLI: "${top}", hook: ${picks.map((p) => p.head).join(' | ') || 'nothing'}`);
  }
}

db.close();
rmSync(STORE, { recursive: true, force: true, maxRetries: 20, retryDelay: 250 });

console.log(`\n${passed} agreed, ${failures.length} diverged`);
if (failures.length > 0) process.exit(1);
