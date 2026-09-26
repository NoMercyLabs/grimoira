// SessionEnd hook: absorb the project's AI-meta docs (.claude/{prd,specs,plans,reference}) into the
// docs channel so doc(...) recall stays current as specs/plans land, instead of waiting for a manual
// `aitm index-docs`. Idempotent (docs keyed by path; index-docs sheds outdated sections), runs the
// prebuilt CLI, and always fails open — a broken index never blocks session exit.
import { existsSync } from 'node:fs';
import { spawnSync } from 'node:child_process';
import { homedir } from 'node:os';
import { join, basename } from 'node:path';
import { publishedCliDll } from './published-cli.mjs';

let input = '';
process.stdin.on('data', (d) => { input += d; });
process.stdin.on('end', () => {
  try {
    const payload = JSON.parse(input || '{}');
    const proj = process.env.CLAUDE_PROJECT_DIR || payload.cwd || process.cwd();
    const claudeDir = join(proj, '.claude');
    if (!existsSync(claudeDir)) process.exit(0);
    const slug = basename(proj.replace(/[\\/]+$/, '')).toLowerCase().replace(/[^a-z0-9_-]/g, '');
    if (!slug || !existsSync(join(homedir(), '.aitm', slug, 'aitm.db'))) process.exit(0);
    spawnSync('dotnet', [
      publishedCliDll(process.env, import.meta.dirname),
      'index-docs',
      '--from', claudeDir,
      '--instance', slug,
    ], { timeout: 120000, stdio: 'ignore', cwd: proj });
  } catch {
    // fail open — never block session end on an index error
  }
  process.exit(0);
});
