// Stop-hook guard: refuses to let the turn end while an enforcement brake is unsatisfied. Two brakes:
//   1. count-loop   — ~/.aitm/<instance>/loop-state.json (written by `aitm loop start/tick`). Blocks while done<total.
//   2. write-side   — ~/.aitm/<instance>/pending-learn.jsonl (written by `aitm stage`). Blocks while it has lines.
// Both are the enforcement passive memory could never provide: Arc cannot rationalize past a blocked stop.
// State clears itself (loop at done==total; ledger on `aitm flush`) or via `aitm loop clear` / `aitm stage clear`.
import { readFileSync, existsSync } from 'node:fs';
import { homedir } from 'node:os';
import { join, basename } from 'node:path';

const proj = process.env.CLAUDE_PROJECT_DIR || process.cwd();
const slug = basename(proj.replace(/[\\/]+$/, '')).toLowerCase().replace(/[^a-z0-9_-]/g, '');
const dir = join(homedir(), '.aitm', slug || 'default');

const blocks = [];

// 1) count-loop brake — an explicit count must be finished, not checkpointed.
try {
  const p = join(dir, 'loop-state.json');
  if (existsSync(p)) {
    const s = JSON.parse(readFileSync(p, 'utf8'));
    if (typeof s.done === 'number' && typeof s.total === 'number' && s.done < s.total) {
      blocks.push(
        `Active count-loop "${s.task}" is ${s.done}/${s.total} — NOT done. the owner gave an explicit count; a ` +
        `fraction-done stop is a disguised stop (he has called this out twice). Do NOT end the turn. Run the ` +
        `next cycle now (improve -> fresh inventory -> revalidate), then run: dotnet run c:\\Projects\\aitm\\aitm.cs ` +
        `loop tick --instance ${slug}. Only stop at ${s.total}/${s.total}, or if the owner says stop (aitm loop clear).`
      );
    }
  }
} catch {
  // never let a guard error block a normal stop
}

// 2) write-side brake — staged knowledge must be persisted, or the brain never gets smarter.
try {
  const p = join(dir, 'pending-learn.jsonl');
  if (existsSync(p)) {
    const lines = readFileSync(p, 'utf8').split('\n').filter((l) => l.trim().length > 0);
    if (lines.length > 0) {
      blocks.push(
        `${lines.length} staged learning(s) NOT yet persisted. "Get smarter over time" fails the instant durable ` +
        `knowledge is dropped — this is exactly the rot that killed MEMORY.md. Commit them now: dotnet run ` +
        `c:\\Projects\\aitm\\aitm.cs flush --instance ${slug}. If on reflection they're junk: aitm stage clear.`
      );
    }
  }
} catch {
  // never let a guard error block a normal stop
}

if (blocks.length > 0) {
  process.stdout.write(JSON.stringify({ decision: 'block', reason: blocks.join('\n\n') }));
}
process.exit(0);
