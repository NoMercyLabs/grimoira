// Stop hook: when a turn reads a whole set of files to answer one question, keep the answer.
//
// brain-capture watches SEARCHES, so a turn that never searched — ls, then twelve Reads — taught the
// store nothing. That is the shape this closes. The compacted sources are only ~7% smaller than the
// originals, so re-serving them saves nothing; what is worth keeping is the distillation. One session
// read 140 KB to write a 15 KB orientation brief and threw the brief away, leaving the next person to
// read the same 140 KB.
//
// Deliberately narrow: several files from ONE directory, and a substantial prose answer. A turn that
// read scattered files was not summarising a set, and a short reply was not a distillation.
import { writeFileSync, mkdirSync, readFileSync, existsSync, rmSync, readdirSync } from 'node:fs';
import { execFileSync } from 'node:child_process';
import { homedir, tmpdir } from 'node:os';
import { join, dirname, resolve, isAbsolute } from 'node:path';
import { resolveInstance, tailEntries } from './brain-lib.mjs';

const MIN_FILES = 3;
const MIN_ANSWER = 1500;
// Share of the directory's prose files the turn must have read for it to count as a set.
const MIN_COVERAGE = 0.5;
const PROSE = /\.(md|mdx|txt|rst|adoc)$/i;
const SCRATCH = /(^|\/)(scratchpad|\.?scratch|node_modules|Temp|tmp)(\/|$)/i;

const cliPath = () => {
  const exe = join(dirname(new URL(import.meta.url).pathname.replace(/^\//, '')), 'bin-cli', 'aitm.exe');
  return existsSync(exe) ? exe : null;
};

function turnOf(transcriptPath) {
  const { entries } = tailEntries(transcriptPath);
  let start = 0;
  for (let i = entries.length - 1; i >= 0; i--) {
    const e = entries[i];
    if (!(e.type === 'user' || e.message?.role === 'user')) continue;
    const c = e.message?.content;
    if (Array.isArray(c) && c.some((b) => b.type === 'tool_result')) continue;
    // A guard's own feedback arrives as a user entry. Treating it as a new prompt cuts the turn in
    // half, and the half left over is the reply to the guard rather than the work being summarised.
    const text = typeof c === 'string' ? c : Array.isArray(c) ? c.filter((b) => b.type === 'text').map((b) => b.text).join('') : '';
    if (e.isMeta || /^(Stop hook feedback|\[Request interrupted)/.test(text.trim())) continue;
    start = i;
    break;
  }
  return entries.slice(start);
}

let input = '';
process.stdin.on('data', (d) => { input += d; });
process.stdin.on('end', () => {
  let tmp = null;
  try {
    const payload = JSON.parse(input || '{}');
    const tp = payload.transcript_path;
    const cli = cliPath();
    if (!tp || !existsSync(tp) || !cli) process.exit(0);

    const turn = turnOf(tp);
    const blocks = turn.flatMap((e) => (Array.isArray(e.message?.content) ? e.message.content : []));

    // Prose only, and only outside scratch. Reading three source files and writing a long explanation is
    // ordinary work, not a document set being summarised — filing that as a "synthesis" would put an
    // answer about code into the docs channel, where the read gate can never serve it and every doc
    // query has to step over it. Scratch directories are throwaway by definition.
    const read = [];
    for (const b of blocks) {
      if (b.type !== 'tool_use' || b.name !== 'Read' || !b.input?.file_path) continue;
      // Same absolute-path normalisation the read gate does: a synthesis filed under a relative
      // directory can never be looked up again, because the gate resolves before it searches.
      const raw = b.input.file_path;
      const f = (isAbsolute(raw) ? raw : resolve(payload.cwd || process.cwd(), raw)).replace(/\\/g, '/');
      if (!PROSE.test(f) || SCRATCH.test(f)) continue;
      read.push(f);
    }
    if (read.length < MIN_FILES) process.exit(0);

    // One directory has to account for the reading, or this was not a sweep over a set.
    const byDir = new Map();
    for (const f of read) {
      const d = dirname(f);
      byDir.set(d, (byDir.get(d) || 0) + 1);
    }
    const [dir, count] = [...byDir.entries()].sort((a, b) => b[1] - a[1])[0];
    if (count < MIN_FILES) process.exit(0);

    // The directory has to be a SET, not a shelf. Three files out of twelve report parts is a sweep of
    // one document; three files out of 412 independent memory notes is a sample of unrelated things,
    // and summarising it produced an answer about encoder dashboard cards that then intercepted every
    // read of every memory file. Coverage is what separates the two, and nothing else does.
    let siblings = 0;
    try {
      siblings = readdirSync(dir).filter((f) => PROSE.test(f)).length;
    } catch { process.exit(0); }
    if (siblings === 0 || count / siblings < MIN_COVERAGE) process.exit(0);

    // The longest reply, not the last. A turn often ends with a short exchange about a guard or a
    // follow-up question, and taking the final text stored that instead of the work.
    let answer = '';
    for (const e of turn) {
      if (!(e.type === 'assistant' || e.message?.role === 'assistant')) continue;
      const t = (e.message.content || []).filter((b) => b.type === 'text').map((b) => b.text).join('').trim();
      if (t.length > answer.length) answer = t;
    }
    if (answer.length < MIN_ANSWER) process.exit(0);

    const instance = resolveInstance(payload);
    const sources = [...new Set(read.filter((f) => dirname(f) === dir))].join(';');

    tmp = join(tmpdir(), `aitm-synthesis-${process.pid}.md`);
    writeFileSync(tmp, answer);
    const out = execFileSync(cli, ['add-synthesis', '--path', dir, '--from', tmp, '--sources', sources, '--instance', instance], { encoding: 'utf8' });
    if (!/^synthesis stored/.test(out.trim())) process.exit(0);

    // Remember which sets already have one, so the read gate can offer it without a store round trip.
    const idx = join(homedir(), '.aitm', instance, 'synthesis.json');
    let known = {};
    try { known = JSON.parse(readFileSync(idx, 'utf8')); } catch { /* first one */ }
    known[dir.toLowerCase()] = { at: new Date().toISOString(), files: count };
    try { mkdirSync(dirname(idx), { recursive: true }); writeFileSync(idx, JSON.stringify(known, null, 2)); } catch { /* best effort */ }

    process.stdout.write(JSON.stringify({
      systemMessage: `aitm: kept your answer as the synthesis for ${dir} (${count} files read). Next time it is served instead of re-reading them.`,
    }));
  } catch {
    // fail open — never block a stop on bookkeeping
  } finally {
    try { if (tmp) rmSync(tmp); } catch { /* already gone */ }
  }
  process.exit(0);
});
