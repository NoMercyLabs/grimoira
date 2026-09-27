#!/usr/bin/env node
// The SessionStart hook of the installed plugin (RESTRUCTURE.md slice 32a).
//
// Build output is gitignored and every plugin version installs into a new cache folder, so right after an
// install or update there is no bin-cli/ or bin-server/. The published CLI and server therefore live in
// CLAUDE_PLUGIN_DATA (kept across plugin updates), guarded by a build stamp like launch-mcp.mjs.
//
// - Build current: run `dotnet <data>/current/bin-cli/grimora.dll hook SessionStart`, stdin, stdout and the exit
//   code passed straight through. The hook gets GRIMORA_PLUGIN_ROOT, so a server it starts from the data folder
//   still finds the files that ship in the plugin root (idp-impersonate.mjs, seeds/).
// - A running server keeps the build it started from. The CLI's SessionStart compares the build stamp in its
//   /health with the current build's: on a mismatch the old server finishes its calls in flight and exits, and
//   the server of the current build starts (ServerHandover, slice 32b).
// - Missing or stale: start build-cli-and-server.mjs detached, print one line, exit 0. A lock file makes
//   sure two sessions never build at once. The session goes on without Grimora until the build is done.
//
// With no CLAUDE_PLUGIN_DATA (run from a checkout) the checkout's own bin-cli/ and bin-server/ are used, as
// launch-mcp.mjs uses the checkout's bin/. build-cli.ps1 and build-server.ps1 own their freshness there, so
// the step only builds when they are missing.

import { promoteLegacyEnv } from './legacy-env.mjs';
import { closeSync, existsSync, mkdirSync, openSync, renameSync, rmSync, statSync, writeFileSync, writeSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { spawn } from 'node:child_process';
import { readStamp, treeHash } from './build-stamp.mjs';
import { liveBuildDir, runCliHook } from './published-cli.mjs';

/** Every input of `dotnet publish` for the CLI and the server: all project sources and the shared build files. */
export const BUILD_INPUTS = ['src', 'Directory.Build.props', 'Directory.Packages.props', 'global.json'];

const LOCK = 'build.lock';
/** Longer than any real build; a lock this old was left by a build that died. */
const LOCK_EXPIRY_MS = 30 * 60 * 1000;

export const BUILDING_LINE = 'Grimora is building its CLI and server in the background (first session after an install or update); it is ready in a few minutes.';

/** The folder holding bin-cli/ and bin-server/: the data folder's current build, else the checkout itself. */
export function buildDir(dataDir, pluginData) {
  return pluginData ? liveBuildDir(dataDir) : dataDir;
}

export function isCurrent(root, dataDir, pluginData) {
  const dir = buildDir(dataDir, pluginData);
  const cliDir = join(dir, 'bin-cli');
  if (!existsSync(join(cliDir, 'grimora.dll')) || !existsSync(join(dir, 'bin-server', 'Grimora.Server.dll'))) return false;
  return !pluginData || readStamp(cliDir) === treeHash(root, BUILD_INPUTS);
}

/** Takes the build lock; false when another session holds it. */
export function takeLock(dataDir) {
  mkdirSync(dataDir, { recursive: true }); // the first session may be the first to use the data folder
  const lock = join(dataDir, LOCK);
  try {
    if (Date.now() - statSync(lock).mtimeMs > LOCK_EXPIRY_MS) rmSync(lock, { force: true });
  } catch { /* no lock yet */ }
  try {
    const fd = openSync(lock, 'wx');
    writeSync(fd, String(Date.now()));
    closeSync(fd);
    return true;
  } catch {
    return false;
  }
}

export function releaseLock(dataDir) {
  rmSync(join(dataDir, LOCK), { force: true });
}

/**
 * Records the plugin root in <data>/plugin-root.txt, written to a temp file and renamed so a reader never sees
 * half a path. A server started without this hook (the logon task, a thin client of another slot) has no
 * GRIMORA_PLUGIN_ROOT and finds idp-impersonate.mjs and seeds/ through this file (PluginFileLocator).
 */
export function recordPluginRoot(dataDir, root) {
  mkdirSync(dataDir, { recursive: true });
  const file = join(dataDir, 'plugin-root.txt');
  const temp = `${file}.${process.pid}.tmp`;
  writeFileSync(temp, root);
  renameSync(temp, file);
}

export function sessionStart({ root, dataDir, pluginData, runHook, startBuild, write }) {
  if (pluginData) {
    try { recordPluginRoot(dataDir, root); } catch { /* best effort: the env var and the walk-up remain */ }
  }
  if (isCurrent(root, dataDir, pluginData)) {
    return runHook(join(buildDir(dataDir, pluginData), 'bin-cli', 'grimora.dll'), { GRIMORA_PLUGIN_ROOT: root });
  }
  if (takeLock(dataDir)) {
    try {
      startBuild(dataDir);
    } catch {
      releaseLock(dataDir);
    }
  }
  write(BUILDING_LINE);
  return 0;
}

// The build must not hold the hook's stdout/stderr pipes: the hook runner reads them to the end, so a child
// that keeps them waits the session for the whole build (slice 29d waited 380 s on a server that did this).
// Node marks its own stdio handles non-inheritable at startup, and stdio 'ignore' gives the child none of them.
export function startDetached(script, args) {
  const child = spawn(process.execPath, [script, ...args], { detached: true, stdio: 'ignore', windowsHide: true });
  child.unref();
}

function main() {
  promoteLegacyEnv();
  const root = import.meta.dirname;
  const pluginData = process.env.CLAUDE_PLUGIN_DATA;
  const code = sessionStart({
    root,
    dataDir: pluginData || root,
    pluginData: Boolean(pluginData),
    runHook: (cli, env) => runCliHook(cli, 'SessionStart', env.GRIMORA_PLUGIN_ROOT),
    startBuild: dataDir => startDetached(join(root, 'build-cli-and-server.mjs'), [dataDir]),
    write: line => process.stdout.write(`${line}\n`),
  });
  process.exitCode = code;
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) main();
