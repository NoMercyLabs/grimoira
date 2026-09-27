#!/usr/bin/env node
// The UserPromptSubmit, PreCompact and SessionEnd hook slots of the installed plugin (RESTRUCTURE.md slice 32a).
//
// Usage: node run-hook.mjs <event>
//
// The published CLI is the data folder's current build, else the checkout's bin-cli (published-cli.mjs).
// - No build yet (the first session after an install, while SessionStart's build runs): print nothing and
//   exit 0. A direct `dotnet <missing dll>` printed dotnet's "Could not execute" block and exited 1 on every
//   prompt. This step never starts a build; only SessionStart does.
// - Built: run `hook <event>` through it, stdin, stdout and the exit code passed straight through. A build
//   that is stale still runs: the new one replaces it only when complete.
import { existsSync } from 'node:fs';
import { resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { promoteLegacyEnv } from './legacy-env.mjs';
import { publishedCliDll, runCliHook } from './published-cli.mjs';

function main() {
  promoteLegacyEnv();
  const event = process.argv[2];
  const root = import.meta.dirname;
  const cli = publishedCliDll(process.env, root);
  process.exitCode = event && existsSync(cli) ? runCliHook(cli, event, root) : 0;
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) main();
