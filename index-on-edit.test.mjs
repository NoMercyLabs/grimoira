// Run the hook with mocked filesystem and subprocess boundaries. Never open a live store.
import { readFileSync } from 'node:fs';
import { runInNewContext } from 'node:vm';
import * as path from 'node:path';
import assert from 'node:assert/strict';
import { test } from 'node:test';
import { publishedCliDll } from './published-cli.mjs';

const source = readFileSync(new URL('./index-on-edit.mjs', import.meta.url), 'utf8')
  .replace(/^import .*;\r?\n/gm, '')
  // import.meta is not reachable from runInNewContext, so it becomes a plain identifier (as in launch-mcp.test.mjs).
  .replace(/import\.meta\.dirname/g, '__dirname');

function invoke(result, env = {}) {
  const handlers = {};
  let stdout = '', stderr = '', calls = 0;
  const spawned = [];
  const exit = Symbol('exit');
  const project = path.resolve('fixture-project');
  const payload = JSON.stringify({ cwd: project,
    tool_input: { file_path: path.join(project, '.claude', 'docs', 'design', 'example.md') } });
  const context = {
    ...path,
    existsSync: () => true,
    publishedCliDll,
    __dirname: path.resolve('fixture-checkout'),
    homedir: () => path.resolve('fixture-home'),
    spawnSync: (cmd, args) => { calls++; spawned.push([cmd, ...args]); if (result instanceof Error) throw result; return result; },
    process: {
      env, cwd: () => project,
      stdin: { on: (event, fn) => { handlers[event] = fn; } },
      stdout: { write: text => { stdout += text; } },
      stderr: { write: text => { stderr += text; } },
      exit: () => { throw exit; },
    },
  };
  runInNewContext(source, context);
  handlers.data(payload);
  try { handlers.end(); } catch (error) { if (error !== exit) throw error; }
  return { stdout, stderr, calls, spawned };
}

test('successful child reports indexing success', () => {
  const output = invoke({ status: 0 });
  assert.equal(output.calls, 1);
  assert.match(output.stdout, /reindexed/);
  assert.equal(output.stderr, '');
});

for (const [name, result] of [
  ['failed exit', { status: 1 }],
  ['timeout', { status: null, error: { code: 'ETIMEDOUT' } }],
  ['signal', { status: null, signal: 'SIGTERM' }],
  ['spawn exception', new Error('private child detail')],
]) {
  test(`${name} never reports success or exposes child output`, () => {
    const output = invoke(result);
    assert.equal(output.calls, 1);
    assert.equal(output.stdout, '');
    assert.match(output.stderr, /index may be stale/);
    assert.doesNotMatch(output.stderr, /private child detail/);
  });
}

// Slice 32a: the installed plugin has its CLI in the data folder's current build, never at a fixed checkout path.
test("reindexes through the data folder's current build when CLAUDE_PLUGIN_DATA is set", () => {
  const data = path.resolve('fixture-data');
  const { spawned } = invoke({ status: 0 }, { CLAUDE_PLUGIN_DATA: data });
  assert.deepEqual(spawned[0].slice(0, 2), ['dotnet', path.join(data, 'current', 'bin-cli', 'aitm.dll')]);
});

test("reindexes through the checkout's own bin-cli beside the script without a plugin data folder", () => {
  const { spawned } = invoke({ status: 0 });
  assert.deepEqual(spawned[0].slice(0, 2), ['dotnet', path.join(path.resolve('fixture-checkout'), 'bin-cli', 'aitm.dll')]);
});
