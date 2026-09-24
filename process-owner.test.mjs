import { test } from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, mkdirSync, readFileSync, rmSync, writeFileSync, existsSync, copyFileSync, readdirSync } from 'node:fs';
import { spawnSync } from 'node:child_process';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { once } from 'node:events';
import { pilotRoot, launchOwned, trimCompleted, inspectOwners } from './process-owner.mjs';

test('pilot excludes sibling workspaces', () => {
  const root = mkdtempSync(join(tmpdir(), 'aitm-owner-'));
  try {
    mkdirSync(join(root, 'NoMercy')); mkdirSync(join(root, 'NoMercy-other'));
    assert.ok(pilotRoot(join(root, 'NoMercy'), join(root, 'NoMercy')));
    assert.equal(pilotRoot(join(root, 'NoMercy-other'), join(root, 'NoMercy')), null);
  } finally { rmSync(root, { recursive: true, force: true }); }
});

test('records normal child exit without command arguments', async () => {
  const root = mkdtempSync(join(tmpdir(), 'aitm-owner-'));
  try {
    const run = launchOwned(process.execPath, ['-e', 'process.exit(0)', 'private-value'], { root, cwd: root, stdio: 'ignore' });
    await once(run.child, 'exit');
    const text = readFileSync(run.filename, 'utf8');
    const record = JSON.parse(text);
    assert.equal(record.state, 'exited'); assert.equal(record.exit_code, 0);
    assert.ok(record.child_pid); assert.equal(record.owner.pid, process.pid);
    assert.equal(run.cancel(), false); assert.ok(!text.includes('private-value'));
  } finally { rmSync(root, { recursive: true, force: true }); }
});

test('cancels only the original live child', async () => {
  const root = mkdtempSync(join(tmpdir(), 'aitm-owner-'));
  let run;
  try {
    run = launchOwned(process.execPath, ['-e', 'setInterval(()=>{},1000)'], { root, cwd: root, stdio: 'ignore' });
    await once(run.child, 'spawn');
    const ended = once(run.child, 'exit');
    assert.equal(run.cancel(), true);
    await ended;
    assert.equal(JSON.parse(readFileSync(run.filename)).state, 'exited');
  } finally { run?.cancel(); rmSync(root, { recursive: true, force: true }); }
});

test('canceling one owner leaves another live child alone', { timeout: 10000 }, async () => {
  const root = mkdtempSync(join(tmpdir(), 'aitm-owner-'));
  const first = launchOwned(process.execPath, ['-e', 'setInterval(()=>{},1000)'], { root, cwd: root, stdio: 'ignore' });
  const second = launchOwned(process.execPath, ['-e', 'setInterval(()=>{},1000)'], { root, cwd: root, stdio: 'ignore' });
  try {
    await Promise.all([once(first.child, 'spawn'), once(second.child, 'spawn')]);
    const firstExit = once(first.child, 'exit');
    assert.equal(first.cancel(), true);
    await firstExit;
    assert.equal(first.child.exitCode !== null || first.child.signalCode !== null, true);
    assert.equal(second.child.exitCode, null);
    assert.equal(second.child.signalCode, null);
    assert.equal(JSON.parse(readFileSync(second.filename)).state, 'running');
    const secondExit = once(second.child, 'exit');
    assert.equal(second.cancel(), true);
    await secondExit;
    assert.equal(JSON.parse(readFileSync(second.filename)).state, 'exited');
  } finally {
    first.cancel(); second.cancel();
    rmSync(root, { recursive: true, force: true });
  }
});

test('failed spawn is recorded', async () => {
  const root = mkdtempSync(join(tmpdir(), 'aitm-owner-'));
  try {
    const run = launchOwned(join(root, 'does-not-exist'), [], { root, cwd: root, stdio: 'ignore' });
    await new Promise(resolve => run.child.once('error', resolve));
    assert.equal(JSON.parse(readFileSync(run.filename)).state, 'failed');
    assert.equal(run.cancel(), false);
  } finally { rmSync(root, { recursive: true, force: true }); }
});

test('retention removes old terminal records but preserves unresolved ownership', () => {
  const root = mkdtempSync(join(tmpdir(), 'aitm-owner-'));
  try {
    const directory = join(root, '.claude', 'scratch', 'process-owners');
    mkdirSync(directory, { recursive: true });
    const paths = [1, 2, 3].map(i => join(directory, `${String(i).padStart(36, '0')}.json`));
    writeFileSync(paths[0], JSON.stringify({ version: 1, state: 'exited', ended_at: '2026-01-01' }));
    writeFileSync(paths[1], JSON.stringify({ version: 1, state: 'failed', ended_at: '2026-01-02' }));
    writeFileSync(paths[2], JSON.stringify({ version: 1, state: 'running' }));
    trimCompleted(root, 1);
    assert.equal(existsSync(paths[0]), false);
    assert.equal(existsSync(paths[1]), true);
    assert.equal(existsSync(paths[2]), true);
    assert.equal(inspectOwners(root, 1).limited, true);
    assert.match(inspectOwners(root).records[0].liveness, /not verified/);
  } finally { rmSync(root, { recursive: true, force: true }); }
});

test('launcher never leaks a temporary copy when mcp.cs is not there to build', () => {
  const root = mkdtempSync(join(tmpdir(), 'aitm-owner-'));
  try {
    const app = join(root, 'aitm');
    const temp = join(root, 'temp');
    mkdirSync(app); mkdirSync(temp);
    for (const file of ['launch-mcp.mjs', 'process-owner.mjs', 'build-stamp.mjs']) {
      copyFileSync(join(import.meta.dirname, file), join(app, file));
    }
    // No mcp.cs beside launch-mcp.mjs and no prebuilt bin/, so the missing-DLL build attempt
    // (added so a fresh plugin install with no committed bin/ can still build itself) fails
    // instead of finding a DLL to shadow-copy. The shadow-copy step must never run either way:
    // check for an aitm-mcp-* shadow dir specifically, since dotnet's own failed build attempt
    // is expected to leave its own unrelated scratch files under the same TEMP.
    const result = spawnSync(process.execPath, [join(app, 'launch-mcp.mjs')], {
      encoding: 'utf8', timeout: 15000,
      env: { ...process.env, TEMP: temp, TMP: temp, TMPDIR: temp },
    });
    assert.equal(result.status, 1);
    assert.match(result.stderr, /build failed/);
    assert.deepEqual(readdirSync(temp).filter(name => name.startsWith('aitm-mcp-')), []);
  } finally { rmSync(root, { recursive: true, force: true }); }
});
