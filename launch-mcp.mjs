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
// stdio is inherited straight through to the dotnet child, so the JSON-RPC stream is byte-for-byte
// transparent — node writes nothing to stdout itself.

import { cpSync, mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { spawn } from 'node:child_process';

const binDir = join(import.meta.dirname, 'bin');
const shadow = mkdtempSync(join(tmpdir(), 'aitm-mcp-'));
cpSync(binDir, shadow, { recursive: true });

const child = spawn('dotnet', [join(shadow, 'mcp.dll'), ...process.argv.slice(2)], { stdio: 'inherit' });

const clean = () => { try { rmSync(shadow, { recursive: true, force: true }); } catch { /* best effort */ } };
child.on('exit', (code) => { clean(); process.exit(code ?? 0); });
child.on('error', (err) => { clean(); process.stderr.write(`launch-mcp: ${err.message}\n`); process.exit(1); });
process.on('SIGTERM', () => child.kill('SIGTERM'));
process.on('SIGINT', () => child.kill('SIGINT'));
