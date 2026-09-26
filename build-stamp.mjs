// Decide whether the tool server must be rebuilt before launch.
//
// launch-mcp.mjs built bin/ only when mcp.dll was missing. The plugin build lives in CLAUDE_PLUGIN_DATA,
// which survives a plugin update, so an update never rebuilt it: graph_query shipped in 0.3.0 and a
// session on the updated plugin still ran the 0.2.0 server without it. The stamp is a hash of the
// server's source; a different hash means the build is stale.
import { createHash } from 'node:crypto';
import { existsSync, readdirSync, readFileSync, statSync, writeFileSync } from 'node:fs';
import { join, relative, sep } from 'node:path';

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

// The CLI and server are built from many files: every file under the given folders and the given files,
// hashed with their relative paths. A folder's bin/ and obj/ are build output, never source, so a publish
// never makes its own build stale.
export function treeHash(root, paths) {
  const hash = createHash('sha256');
  const files = [];
  const walk = path => {
    if (!existsSync(path)) return;
    if (!statSync(path).isDirectory()) { files.push(path); return; }
    for (const entry of readdirSync(path, { withFileTypes: true })) {
      if (entry.isDirectory() && (entry.name === 'bin' || entry.name === 'obj')) continue;
      walk(join(path, entry.name));
    }
  };
  for (const path of paths) walk(join(root, path));
  for (const file of files.map(f => relative(root, f).split(sep).join('/')).sort()) {
    hash.update(file).update('\0').update(readFileSync(join(root, file))).update('\0');
  }
  return hash.digest('hex');
}

export function readStamp(binDir) {
  const stampFile = join(binDir, STAMP);
  return existsSync(stampFile) ? readFileSync(stampFile, 'utf8').trim() : null;
}

export function writeHashStamp(binDir, hash) {
  writeFileSync(join(binDir, STAMP), hash);
}
