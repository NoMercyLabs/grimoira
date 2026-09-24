// Run the launcher with mocked filesystem and subprocess boundaries. Never spawn a real dotnet
// process or touch the real bin/ — only prove the missing-bin build branch fires with the right args.
import { readFileSync } from 'node:fs';
import { runInNewContext } from 'node:vm';
import * as path from 'node:path';
import assert from 'node:assert/strict';
import { test } from 'node:test';

const source = readFileSync(new URL('./launch-mcp.mjs', import.meta.url), 'utf8')
  .replace(/^import .*;\r?\n/gm, '')
  .replace(/^#!.*\r?\n/, '');

function invoke({ dllExists, buildResult, dataDir } = {}) {
  const pluginRoot = path.resolve('fixture-plugin');
  const calls = { mkdir: [], spawnSync: [], cpSync: [], spawn: [] };
  const exit = Symbol('exit');
  let exitCode;
  const context = {
    ...path,
    // node:fs
    existsSync: p => (String(p).endsWith('mcp.dll') ? Boolean(dllExists) : true),
    mkdirSync: (p, opts) => calls.mkdir.push({ p, opts }),
    mkdtempSync: prefix => `${prefix}fixture`,
    cpSync: (from, to) => calls.cpSync.push({ from, to }),
    rmSync: () => {},
    // node:os
    tmpdir: () => path.resolve('fixture-tmp'),
    // node:child_process
    spawnSync: (cmd, args, opts) => { calls.spawnSync.push({ cmd, args, opts }); return buildResult ?? { status: 0 }; },
    spawn: (cmd, args, opts) => {
      calls.spawn.push({ cmd, args, opts });
      return { on: () => {}, once: () => {} };
    },
    // process-owner.mjs stand-in — never exercise the real ownership pilot here.
    pilotRoot: () => null,
    launchOwned: () => null,
    process: {
      env: dataDir ? { CLAUDE_PLUGIN_DATA: dataDir } : {},
      argv: ['node', 'launch-mcp.mjs'],
      cwd: () => pluginRoot,
      on: () => {},
      stderr: { write: () => {} },
      exit: code => { exitCode = code; throw exit; },
    },
  };
  // The source references `import.meta.dirname` directly, which is not reachable from
  // runInNewContext, so substitute a plain identifier before running it.
  const patched = source.replace(/import\.meta\.dirname/g, '__dirname');
  try {
    runInNewContext(patched, { ...context, __dirname: pluginRoot });
  } catch (error) {
    if (error !== exit) throw error;
  }
  return { calls, exitCode, pluginRoot };
}

// build.args is an array from inside the vm realm, so a strict deepEqual against a normal-realm
// array fails on constructor identity alone even when every element matches. Compare via JSON.
const list = arrayLike => JSON.stringify(Array.from(arrayLike));

test('builds mcp.dll into bin/ next to the script when it is missing and CLAUDE_PLUGIN_DATA is unset', () => {
  const { calls, pluginRoot } = invoke({ dllExists: false, buildResult: { status: 0 } });
  assert.equal(calls.spawnSync.length, 1);
  const [build] = calls.spawnSync;
  assert.equal(build.cmd, 'dotnet');
  assert.equal(list(Array.from(build.args).slice(0, 2)), list(['build', path.join(pluginRoot, 'mcp.cs')]));
  assert.equal(list(Array.from(build.args).slice(2)), list(['-c', 'Release', '-o', path.join(pluginRoot, 'bin')]));
});

test('builds into CLAUDE_PLUGIN_DATA/bin when that env var is set, never the checkout', () => {
  const dataDir = path.resolve('fixture-plugin-data');
  const { calls } = invoke({ dllExists: false, buildResult: { status: 0 }, dataDir });
  const [build] = calls.spawnSync;
  assert.equal(list(Array.from(build.args).slice(2)), list(['-c', 'Release', '-o', path.join(dataDir, 'bin')]));
});

test('skips the build entirely when mcp.dll already exists', () => {
  const { calls } = invoke({ dllExists: true });
  assert.equal(calls.spawnSync.length, 0);
});

test('exits non-zero and never launches when the build fails', () => {
  const { calls, exitCode } = invoke({ dllExists: false, buildResult: { status: 1 } });
  assert.equal(exitCode, 1);
  assert.equal(calls.spawn.length, 0);
  assert.equal(calls.cpSync.length, 0);
});
