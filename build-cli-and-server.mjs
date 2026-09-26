#!/usr/bin/env node
// Builds the published CLI and server into a plugin data folder (RESTRUCTURE.md slice 32a). session-start.mjs
// starts it detached, holding the build lock; this script frees the lock when it ends.
//
// Usage: node build-cli-and-server.mjs <data folder>
//
// A running server holds the files of the build it started from, and a live hook or headersHelper holds
// bin-cli/ files, so a build never writes into a folder that may be in use:
// - Each build goes into its own folder, <data>/builds/<first 12 hex of the stamp>/{bin-cli,bin-server}, with
//   the same publish commands as build-cli.ps1 and build-server.ps1. The stamp is written last.
// - <data>/current (a directory junction on Windows, a symlink elsewhere) moves to it only then, so it never
//   points at a half-built folder.
// - The previous build stays as the rollback. Older ones are deleted only when a trial rename proves no process
//   holds them (Windows refuses to rename a folder in use); a held one is tried again on the next build.

import { closeSync, lstatSync, mkdirSync, openSync, readdirSync, realpathSync, renameSync, rmSync,
  symlinkSync, unlinkSync, writeSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { spawnSync } from 'node:child_process';
import { readStamp, treeHash, writeHashStamp } from './build-stamp.mjs';
import { BUILD_INPUTS, releaseLock } from './session-start.mjs';
import { liveBuildDir } from './published-cli.mjs';

const DELETING = '.deleting';

function realOrNull(path) {
  try { return realpathSync(path); } catch { return null; }
}

/** Points <data>/current at a complete build. */
export function pointCurrent(dataDir, target) {
  const link = liveBuildDir(dataDir);
  if (process.platform === 'win32') {
    // A junction needs no admin rights. It cannot be renamed over an existing one, so it is replaced; a hook
    // starting in that moment fails open and the next one runs.
    if (lstatOrNull(link)) unlinkSync(link);
    symlinkSync(target, link, 'junction');
  } else {
    const next = `${link}.next`;
    rmSync(next, { force: true });
    symlinkSync(target, next, 'dir');
    renameSync(next, link); // atomic replace
  }
}

function lstatOrNull(path) {
  try { return lstatSync(path); } catch { return null; }
}

/** Deletes every build except those kept, each only if a trial rename shows nothing holds it. */
export function removeOldBuilds(buildsDir, keep) {
  const kept = new Set(keep.filter(Boolean).map(p => resolve(p).toLowerCase()));
  for (const name of readdirSync(buildsDir)) {
    const folder = join(buildsDir, name);
    if (kept.has(resolve(folder).toLowerCase())) continue;
    try {
      const doomed = name.endsWith(DELETING) ? folder : `${folder}${DELETING}`;
      if (doomed !== folder) renameSync(folder, doomed);
      rmSync(doomed, { recursive: true, force: true });
    } catch {
      // held by a running server or hook: kept until a later build
    }
  }
}

export function buildAndPoint({ root, dataDir, publish }) {
  try {
    const hash = treeHash(root, BUILD_INPUTS);
    const buildsDir = join(dataDir, 'builds');
    const target = join(buildsDir, hash.slice(0, 12));
    if (readStamp(join(target, 'bin-cli')) !== hash) {
      rmSync(target, { recursive: true, force: true }); // an unfinished earlier try: never current, never in use
      publish(join(root, 'src', 'Aitm.Cli', 'Aitm.Cli.csproj'), join(target, 'bin-cli'));
      publish(join(root, 'src', 'Aitm.Server', 'Aitm.Server.csproj'), join(target, 'bin-server'));
      writeHashStamp(join(target, 'bin-cli'), hash);
    }
    const previous = realOrNull(liveBuildDir(dataDir));
    pointCurrent(dataDir, target);
    removeOldBuilds(buildsDir, [realOrNull(target), target, previous]);
  } finally {
    releaseLock(dataDir);
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
    buildAndPoint({ root, dataDir, publish });
  } catch (error) {
    // Started with stdio 'ignore', so the log is the only place a failure shows.
    writeSync(log, `build-cli-and-server: ${error.message}\n`);
    process.exitCode = 1;
  } finally {
    closeSync(log);
  }
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) main();
