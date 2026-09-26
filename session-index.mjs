// SessionEnd hook: fold the session's transcript into the chat-recall channel automatically, so
// recall("what did we decide N sessions ago") is always current instead of waiting for a manual
// `aitm index-chat`. Indexing is idempotent (chat.k is a primary key), runs the prebuilt CLI
// (bin-cli, ~0.2s startup), and always fails open — a broken index never blocks session exit.
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
    const transcript = payload.transcript_path;
    if (!transcript || !existsSync(transcript)) process.exit(0);
    const proj = process.env.CLAUDE_PROJECT_DIR || payload.cwd || process.cwd();
    const slug = basename(proj.replace(/[\\/]+$/, '')).toLowerCase().replace(/[^a-z0-9_-]/g, '');
    if (!slug || !existsSync(join(homedir(), '.aitm', slug, 'aitm.db'))) process.exit(0);
    spawnSync('dotnet', [
      publishedCliDll(process.env, import.meta.dirname),
      'index-chat',
      '--from', transcript,
      '--instance', slug,
    ], { timeout: 60000, stdio: 'ignore' });
  } catch {
    // fail open — never block session end on an index error
  }
  process.exit(0);
});
