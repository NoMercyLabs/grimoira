import test from 'node:test';
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { mkdtempSync, mkdirSync, realpathSync, rmSync, writeFileSync } from 'node:fs';
import { homedir, tmpdir } from 'node:os';
import { dirname, join } from 'node:path';

test('workspace search is registered and closes the helper input stream', async () => {
  const root = mkdtempSync(join(tmpdir(), 'aitm-workspace-tools-'));
  const scripts = join(root, 'scripts');
  mkdirSync(scripts);
  // A helper that waits for input EOF catches the stall caused by inheriting
  // the MCP stream. The real search behavior is covered in the workspace repo.
  writeFileSync(join(scripts, 'workspace-search.py'),
    'import sys\nsys.stdin.read()\nprint("src/one.py:1")\n');
  const child = spawn('dotnet', [join(import.meta.dirname, 'bin', 'mcp.dll')], {
    cwd: root,
    env: { ...process.env, CLAUDE_PROJECT_DIR: root, AITM_INSTANCE: `workspace-tool-test-${process.pid}` },
    stdio: ['pipe', 'pipe', 'ignore'],
  });
  const pending = new Map();
  const failPending = error => {
    for (const resolve of pending.values()) resolve({ error });
    pending.clear();
  };
  child.on('error', failPending);
  let buffer = '';
  child.stdout.on('data', chunk => {
    buffer += chunk;
    while (buffer.includes('\n')) {
      const index = buffer.indexOf('\n');
      const line = buffer.slice(0, index);
      buffer = buffer.slice(index + 1);
      if (!line.trim()) continue;
      const message = JSON.parse(line);
      pending.get(message.id)?.(message);
      pending.delete(message.id);
    }
  });
  const request = (id, method, params) => new Promise(resolve => {
    pending.set(id, resolve);
    child.stdin.write(JSON.stringify({ jsonrpc: '2.0', id, method, params }) + '\n');
  });
  const exited = new Promise(resolve => child.on('exit', code => {
    failPending(`server exited ${code}`);
    resolve(code);
  }));
  const timer = setTimeout(() => { failPending('tool call timed out'); child.kill(); }, 12000);
  try {
    const init = await request(1, 'initialize', {
      protocolVersion: '2024-11-05', capabilities: {},
      clientInfo: { name: 'workspace-tool-test', version: '1' },
    });
    assert.ok(init.result);
    child.stdin.write(JSON.stringify({ jsonrpc: '2.0', method: 'notifications/initialized' }) + '\n');
    const listed = await request(2, 'tools/list', {});
    assert.ok(listed.result.tools.some(tool => tool.name === 'workspace_search'));
    const result = await request(3, 'tools/call', {
      name: 'workspace_search', arguments: { repository: '.', pattern: 'needle' },
    });
    assert.equal(result.result.isError, undefined);
    assert.match(JSON.stringify(result.result), /src\/one\.py:1/);
    child.stdin.end();
    assert.equal(await exited, 0);
  } finally {
    clearTimeout(timer);
    if (child.exitCode === null) {
      child.kill();
      let waitTimer;
      await Promise.race([exited, new Promise(resolve => { waitTimer = setTimeout(resolve, 1000); })]);
      clearTimeout(waitTimer);
    }
    if (dirname(realpathSync(root)) === realpathSync(tmpdir())) rmSync(root, { recursive: true, force: true });
    rmSync(join(homedir(), '.aitm', `workspace-tool-test-${process.pid}`), { recursive: true, force: true, maxRetries: 20, retryDelay: 250 });
  }
});
