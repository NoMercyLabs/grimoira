// Where the published AITM CLI lives (RESTRUCTURE.md slice 32a).
//
// An installed plugin builds into CLAUDE_PLUGIN_DATA: each build in <data>/builds/<stamp>/{bin-cli,bin-server},
// and <data>/current points at the newest complete one (session-start.mjs, build-cli-and-server.mjs). Run from a
// checkout, with no CLAUDE_PLUGIN_DATA, the CLI is the checkout's own bin-cli/ from build-cli.ps1.
import { join } from 'node:path';

/** The folder holding bin-cli/ and bin-server/ of the build in use. */
export function liveBuildDir(dataDir) {
  return join(dataDir, 'current');
}

export function publishedCliDll(env, checkoutRoot) {
  const dataDir = env.CLAUDE_PLUGIN_DATA;
  return dataDir ? join(liveBuildDir(dataDir), 'bin-cli', 'aitm.dll') : join(checkoutRoot, 'bin-cli', 'aitm.dll');
}
