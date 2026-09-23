// Run the hook with mocked filesystem and subprocess boundaries. Never open a live store.
import { readFileSync } from 'node:fs';
import { runInNewContext } from 'node:vm';
import * as path from 'node:path';
import assert from 'node:assert/strict';
import { test } from 'node:test';

const source = readFileSync(new URL('./index-on-edit.mjs', import.meta.url), 'utf8')
  .replace(/^import .*;\r?\n/gm, '');

function invoke(result) {
  const handlers = {};
  let stdout = '', stderr = '', calls = 0;
  const exit = Symbol('exit');
  const project = path.resolve('fixture-project');
  const payload = JSON.stringify({ cwd: project,
    tool_input: { file_path: path.join(project, '.claude', 'docs', 'design', 'example.md') } });
  const context = {
    ...path,
    existsSync: () => true,
    homedir: () => path.resolve('fixture-home'),
    spawnSync: () => { calls++; if (result instanceof Error) throw result; return result; },
    process: {
      env: {}, cwd: () => project,
      stdin: { on: (event, fn) => { handlers[event] = fn; } },
      stdout: { write: text => { stdout += text; } },
      stderr: { write: text => { stderr += text; } },
      exit: () => { throw exit; },
    },
  };
  runInNewContext(source, context);
  handlers.data(payload);
  try { handlers.end(); } catch (error) { if (error !== exit) throw error; }
  return { stdout, stderr, calls };
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
