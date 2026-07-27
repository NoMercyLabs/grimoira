// PostToolUse hook (Write|Edit|MultiEdit|NotebookEdit): keep the aitm brain's memory + docs channels
// from silently drifting. Recall is hook-enforced but writes weren't — a memory `.md` or design spec
// could be edited and nothing re-indexed it until a manual CLI run, so the brain lagged reality. After
// any successful file edit this re-indexes the touched channel automatically.
//
// Routing (decided purely from the edited path, never reconstructed — the Claude Code project-encoding
// is case-unstable so it can't be rebuilt from the project dir):
//   • edited file is a `*.md` under `~/.claude/projects/<encoded>/memory/`  -> index-memory --from <that memory dir>
//   • edited file is under `<project>/.claude/docs/design/`                 -> index-docs   --from <that design dir>
//   • anything else (source, config, MEMORY.md itself)                     -> silent no-op, exit 0
//
// Instance is resolved the same way every other aitm hook does it: basename(CLAUDE_PROJECT_DIR|cwd),
// lowercased, sanitized. For the memory case we additionally require the encoded `projects/<encoded>`
// segment to end with that slug, so a cross-project memory edit can never index into the wrong instance.
// Best-effort throughout: any error (and the no-op path) exits 0 — a reindex must never break the edit.
import { existsSync } from 'node:fs';
import { spawnSync } from 'node:child_process';
import { homedir } from 'node:os';
import { join, basename, dirname, resolve, sep } from 'node:path';

const AITM_EXE = 'C:/Projects/aitm/bin-cli/aitm.exe';

// Case-insensitive (Windows) path-containment: is `child` inside `parent`?
const norm = (p) => resolve(p).replace(/[\\/]+/g, sep).replace(/[\\/]+$/, '').toLowerCase();
const isInside = (child, parent) => {
  const c = norm(child);
  const d = norm(parent);
  return c === d || c.startsWith(d + sep.toLowerCase());
};

let input = '';
process.stdin.on('data', (d) => { input += d; });
process.stdin.on('end', () => {
  try {
    const payload = JSON.parse(input || '{}');

    // The edited file path lives on the tool call payload (Claude Code passes tool_input).
    const filePath = payload.tool_input?.file_path
      || payload.tool_input?.notebook_path
      || payload.tool_input?.path;
    if (!filePath || typeof filePath !== 'string') process.exit(0);

    // Instance == project slug, exactly the convention the other aitm hooks use.
    const proj = process.env.CLAUDE_PROJECT_DIR || payload.cwd || process.cwd();
    const slug = basename(proj.replace(/[\\/]+$/, '')).toLowerCase().replace(/[^a-z0-9_-]/g, '');
    if (!slug || !existsSync(join(homedir(), '.aitm', slug, 'aitm.db'))) process.exit(0);

    const file = resolve(filePath);

    // (1) Memory channel: a *.md under ~/.claude/projects/<encoded>/memory/ — but skip MEMORY.md
    // (the always-loaded index, which index-memory itself skips). The --from dir is the file's own
    // memory dir, read straight off the edited path so we never rebuild the case-unstable encoding.
    const projectsRoot = join(homedir(), '.claude', 'projects');
    const memoryDir = dirname(file);
    if (
      file.toLowerCase().endsWith('.md') &&
      basename(file).toLowerCase() !== 'memory.md' &&
      basename(memoryDir).toLowerCase() === 'memory' &&
      isInside(memoryDir, projectsRoot)
    ) {
      // Guard: the encoded `projects/<encoded>` segment must belong to this instance, so a memory
      // edit in another project never indexes into this one's brain.
      const encoded = basename(dirname(memoryDir)).toLowerCase();
      if (encoded.endsWith(slug)) {
        reindex('index-memory', memoryDir, slug);
      }
      process.exit(0);
    }

    // (2) Docs channel: anything under <project>/.claude/docs/design/. --from is that design dir.
    const designDir = join(proj, '.claude', 'docs', 'design');
    if (existsSync(designDir) && isInside(file, designDir)) {
      reindex('index-docs', designDir, slug);
      process.exit(0);
    }

    // (3) Everything else — fast silent no-op.
  } catch {
    // fail open — a reindex error must never break the user's edit
  }
  process.exit(0);
});

// Run the prebuilt CLI: <command> --from <dir> --instance <inst> (command FIRST, --instance LAST —
// the reverse makes the CLI treat --instance as the command and dump help). Best-effort, never throws.
function reindex(command, fromDir, instance) {
  try {
    spawnSync(AITM_EXE, [command, '--from', fromDir, '--instance', instance], {
      timeout: 60000,
      stdio: 'ignore',
    });
    process.stdout.write(`aitm ${command}: reindexed ${fromDir} -> ${instance}\n`);
  } catch {
    // swallow — best-effort reindex
  }
}
