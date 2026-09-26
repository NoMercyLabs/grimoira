#!/usr/bin/env node
// Builds the published CLI and server into a plugin data folder (RESTRUCTURE.md slice 32a). session-start.mjs
// starts it detached, holding the build lock; this script frees the lock when it ends.
//
// Usage: node build-cli-and-server.mjs <data folder>
//
// The build goes into <data>/build-next/ first, with the same publish commands as build-cli.ps1 and
// build-server.ps1, and is swapped in only when both publishes succeed. bin-server/ is swapped before bin-cli/,
// because the stamp lives in bin-cli/: a half-done swap leaves the build stale, never falsely current.
// A swap fails while a folder is in use (a running server holds bin-server/ on Windows); the stamped
// build-next/ then stays, and the next build swaps it in without publishing again.

import { closeSync, existsSync, mkdirSync, openSync, readdirSync, renameSync, rmSync, writeSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { spawnSync } from 'node:child_process';
import { readStamp, treeHash, writeHashStamp } from './build-stamp.mjs';
import { BUILD_INPUTS, releaseLock } from './session-start.mjs';

const REPLACED = '.replaced-';

/** Moves <next>/<name> to <data>/<name>. Throws, changing nothing, when the current folder is in use. */
export function swapIn(nextDir, dataDir, name) {
  const built = join(nextDir, name);
  if (!existsSync(built)) return;
  const live = join(dataDir, name);
  const replaced = `${live}${REPLACED}${Date.now()}`;
  if (existsSync(live)) renameSync(live, replaced);
  renameSync(built, live);
  rmSync(replaced, { recursive: true, force: true });
}

export function buildAndSwap({ root, dataDir, publish, swap = swapIn }) {
  try {
    removeReplacedLeftovers(dataDir);
    const hash = treeHash(root, BUILD_INPUTS);
    const nextDir = join(dataDir, 'build-next');
    if (readStamp(join(nextDir, 'bin-cli')) !== hash) {
      rmSync(nextDir, { recursive: true, force: true });
      publish(join(root, 'src', 'Aitm.Cli', 'Aitm.Cli.csproj'), join(nextDir, 'bin-cli'));
      publish(join(root, 'src', 'Aitm.Server', 'Aitm.Server.csproj'), join(nextDir, 'bin-server'));
      writeHashStamp(join(nextDir, 'bin-cli'), hash);
    }
    swap(nextDir, dataDir, 'bin-server');
    swap(nextDir, dataDir, 'bin-cli');
    rmSync(nextDir, { recursive: true, force: true });
  } finally {
    releaseLock(dataDir);
  }
}

// A replaced folder that was still in use when its swap ran is removed on a later build.
function removeReplacedLeftovers(dataDir) {
  for (const entry of readdirSync(dataDir)) {
    if (entry.includes(REPLACED)) rmSync(join(dataDir, entry), { recursive: true, force: true });
  }
}

function main() {
  const dataDir = process.argv[2];
  const root = import.meta.dirname;
  mkdirSync(dataDir, { recursive: true });
  const log = openSync(join(dataDir, 'build.log'), 'w');
  const publish = (project, out) => {
    // PublishAot=false: the same choice as build-cli.ps1 and build-server.ps1.
    const result = spawnSync('dotnet', ['publish', project, '-c', 'Release', '-o', out, '-p:PublishAot=false'],
      { stdio: ['ignore', log, log], windowsHide: true });
    if (result.error || result.status !== 0) throw new Error(`dotnet publish ${project} failed (exit ${result.status}); see build.log`);
  };
  try {
    buildAndSwap({ root, dataDir, publish });
  } catch (error) {
    // Started with stdio 'ignore', so the log is the only place a failure shows.
    writeSync(log, `build-cli-and-server: ${error.message}\n`);
    process.exitCode = 1;
  } finally {
    closeSync(log);
  }
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) main();
