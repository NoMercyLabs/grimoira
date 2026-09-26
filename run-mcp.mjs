#!/usr/bin/env node
// The MCP server slot of the installed plugin (.mcp.json), RESTRUCTURE.md Slice P1 and slice 32a.
//
// Usage: node run-mcp.mjs
//
// The published CLI is the data folder's current build, else the checkout's bin-cli (published-cli.mjs).
// - No build yet (the first session after an install, while SessionStart's build runs): print nothing and exit 0,
//   so the MCP connection just does not appear until the first build is done. This step never starts a build.
// - Built: run `mcp` through it, stdin, stdout and the exit code passed straight through (stdout is the protocol).
import { existsSync } from 'node:fs';
import { resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { publishedCliDll, runCliMcp } from './published-cli.mjs';

function main() {
  const root = import.meta.dirname;
  const cli = publishedCliDll(process.env, root);
  process.exitCode = existsSync(cli) ? runCliMcp(cli, root) : 0;
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) main();
