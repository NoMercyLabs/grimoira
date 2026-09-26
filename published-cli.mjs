// Where the published AITM CLI lives (RESTRUCTURE.md slice 32a).
//
// An installed plugin builds into CLAUDE_PLUGIN_DATA: each build in <data>/builds/<stamp>/{bin-cli,bin-server},
// and <data>/current points at the newest complete one (session-start.mjs, build-cli-and-server.mjs). Run from a
// checkout, with no CLAUDE_PLUGIN_DATA, the CLI is the checkout's own bin-cli/ from build-cli.ps1.
import { join } from 'node:path';
import { spawnSync } from 'node:child_process';

/** The folder holding bin-cli/ and bin-server/ of the build in use. */
export function liveBuildDir(dataDir) {
  return join(dataDir, 'current');
}

export function publishedCliDll(env, checkoutRoot) {
  const dataDir = env.CLAUDE_PLUGIN_DATA;
  return dataDir ? join(liveBuildDir(dataDir), 'bin-cli', 'aitm.dll') : join(checkoutRoot, 'bin-cli', 'aitm.dll');
}

/**
 * Runs `dotnet <cli> hook <event>` with stdin, stdout and stderr inherited, so the hook's input and output pass
 * through byte for byte, and returns its exit code. The plugin root goes along as AITM_PLUGIN_ROOT, so a server
 * the hook starts finds the plugin files. A dotnet that cannot start fails open (0).
 */
export function runCliHook(cli, event, pluginRoot) {
  const hook = spawnSync('dotnet', [cli, 'hook', event],
    { stdio: 'inherit', windowsHide: true, env: { ...process.env, AITM_PLUGIN_ROOT: pluginRoot } });
  return hook.error ? 0 : (hook.status ?? 0);
}

/**
 * Runs `dotnet <cli> mcp` (the stdio MCP server) with stdin, stdout and stderr inherited: stdout is the MCP
 * protocol stream, so nothing else may be written to it. Returns the server's exit code; a dotnet that cannot
 * start ends quietly (0), so the MCP connection just does not appear.
 */
export function runCliMcp(cli, pluginRoot) {
  const server = spawnSync('dotnet', [cli, 'mcp'],
    { stdio: 'inherit', windowsHide: true, env: { ...process.env, AITM_PLUGIN_ROOT: pluginRoot } });
  return server.error ? 0 : (server.status ?? 0);
}
