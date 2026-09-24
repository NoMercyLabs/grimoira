#!/usr/bin/env node
// Launch the aitm MCP server from a per-process SHADOW COPY of bin/, so the canonical bin/mcp.dll is
// never held open by a running session.
//
// The problem this fixes: the server was registered as `dotnet bin/mcp.dll`, so every live Claude
// session's .NET host locks bin/mcp.dll in place. build-mcp.ps1 writes to that same path, so a rebuild
// can NEVER copy while any session is up — and a brand-new tool (e.g. idp_token) silently never
// reaches a running server. Copying the built output to a throwaway temp dir per launch means the source
// binary stays free to rebuild at any time; each session runs its own copy and cleans it up on exit.
//
// As a plugin, bin/ is gitignored and never shipped, so a fresh install has no prebuilt DLL. If the
// output directory has no mcp.dll yet, build it once before the first launch. CLAUDE_PLUGIN_DATA (a
// per-plugin, update-safe directory Claude Code provides) holds that first build when set, so a plugin
// update never clobbers it and the checkout itself stays clean; otherwise it falls back to the existing
// bin/ next to this script, same as build-mcp.ps1 already writes.
//
// stdio is inherited straight through to the dotnet child, so the JSON-RPC stream is byte-for-byte
// transparent — node writes nothing to stdout itself.

import { cpSync, mkdtempSync, mkdirSync, existsSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { spawn, spawnSync } from 'node:child_process';
import { pilotRoot, launchOwned } from './process-owner.mjs';

const dataDir = process.env.CLAUDE_PLUGIN_DATA;
const binDir = dataDir ? join(dataDir, 'bin') : join(import.meta.dirname, 'bin');

if (!existsSync(join(binDir, 'mcp.dll'))) {
  mkdirSync(binDir, { recursive: true });
  const build = spawnSync('dotnet',
    ['build', join(import.meta.dirname, 'mcp.cs'), '-c', 'Release', '-o', binDir],
    { stdio: 'inherit' });
  if (build.error || build.status !== 0) {
    process.stderr.write('launch-mcp: build failed; run build-mcp.ps1 and check the .NET SDK is installed\n');
    process.exit(build.status ?? 1);
  }
}

const shadow = mkdtempSync(join(tmpdir(), 'aitm-mcp-'));
const clean = () => { try { rmSync(shadow, { recursive: true, force: true }); } catch { /* best effort */ } };
try {
  cpSync(binDir, shadow, { recursive: true });
} catch {
  clean();
  process.stderr.write('launch-mcp: could not prepare server files; build AITM before launching\n');
  process.exit(1);
}

const project = process.env.CLAUDE_PROJECT_DIR || process.cwd();
const workspace = join(import.meta.dirname, '..', 'NoMercy');
const root = pilotRoot(project, workspace);
const args = [join(shadow, 'mcp.dll'), ...process.argv.slice(2)];
const owned = root ? launchOwned('dotnet', args, {
  root, cwd: process.cwd(),
  onWarning: message => process.stderr.write(`launch-mcp: ${message}\n`),
}) : null;
const child = owned?.child || spawn('dotnet', args, { stdio: 'inherit' });

child.on('exit', (code, signal) => { clean(); process.exit(code ?? (signal ? 1 : 0)); });
child.on('error', (err) => { clean(); process.stderr.write(`launch-mcp: ${err.message}\n`); process.exit(1); });
process.on('SIGTERM', () => owned ? owned.cancel('SIGTERM') : child.kill('SIGTERM'));
process.on('SIGINT', () => owned ? owned.cancel('SIGINT') : child.kill('SIGINT'));
