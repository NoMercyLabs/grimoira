// Decide whether the tool server must be rebuilt before launch.
//
// launch-mcp.mjs built bin/ only when mcp.dll was missing. The plugin build lives in CLAUDE_PLUGIN_DATA,
// which survives a plugin update, so an update never rebuilt it: graph_query shipped in 0.3.0 and a
// session on the updated plugin still ran the 0.2.0 server without it. The stamp is a hash of the
// server's source; a different hash means the build is stale.
import { createHash } from 'node:crypto';
import { existsSync, readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';

const STAMP = 'build-stamp.txt';

export function sourceHash(sourceFile) {
  return createHash('sha256').update(readFileSync(sourceFile)).digest('hex');
}

export function needsBuild(binDir, sourceFile) {
  if (!existsSync(join(binDir, 'mcp.dll'))) return true;
  if (!existsSync(sourceFile)) return false; // nothing to rebuild from: run the build that exists
  const stampFile = join(binDir, STAMP);
  if (!existsSync(stampFile)) return true;
  return readFileSync(stampFile, 'utf8').trim() !== sourceHash(sourceFile);
}

export function writeStamp(binDir, sourceFile) {
  writeFileSync(join(binDir, STAMP), sourceHash(sourceFile));
}
