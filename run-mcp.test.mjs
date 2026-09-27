// .mcp.json runs run-mcp.mjs. During the first build there is no <data>/current yet: the MCP connection just
// does not appear, so the step prints nothing and exits 0 (no dotnet "Could not execute" block on stdout,
// which would be junk on the protocol stream). Built: the CLI's `mcp` verb runs with stdin, stdout and the
// exit code passed straight through.
import { existsSync, mkdtempSync, readdirSync, rmSync, symlinkSync, unlinkSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { spawnSync } from 'node:child_process';
import assert from 'node:assert/strict';
import { test } from 'node:test';

const script = join(import.meta.dirname, 'run-mcp.mjs');

test('no current build prints nothing, exits 0 and starts no build', () => {
  const dataDir = mkdtempSync(join(tmpdir(), 'grimora-run-mcp-'));
  const store = mkdtempSync(join(tmpdir(), 'grimora-run-mcp-store-'));
  try {
    const env = { ...process.env, CLAUDE_PLUGIN_DATA: dataDir, GRIMORA_DATA_DIR: store };
    const result = spawnSync(process.execPath, [script], { input: '', encoding: 'utf8', env, timeout: 20000 });
    assert.equal(result.status, 0);
    assert.equal(result.stdout, '');
    assert.equal(result.stderr, '');
    assert.deepEqual(readdirSync(dataDir), []);
  } finally {
    rmSync(dataDir, { recursive: true, force: true });
    rmSync(store, { recursive: true, force: true });
  }
});

const built = existsSync(join(import.meta.dirname, 'bin-cli', 'grimora.dll'));
test('with a current build the server exit code passes through and stdout stays clean',
  { skip: !built && 'needs bin-cli/ from build-cli.ps1' }, () => {
    const dataDir = mkdtempSync(join(tmpdir(), 'grimora-run-mcp-'));
    const store = mkdtempSync(join(tmpdir(), 'grimora-run-mcp-store-'));
    try {
      symlinkSync(import.meta.dirname, join(dataDir, 'current'), process.platform === 'win32' ? 'junction' : 'dir');
      // A server exe that does not exist: `grimora mcp` cannot start the service and exits 1, printing nothing to stdout.
      const env = { ...process.env, CLAUDE_PLUGIN_DATA: dataDir, GRIMORA_DATA_DIR: store, GRIMORA_SERVER_EXE: join(store, 'none.exe') };
      const result = spawnSync(process.execPath, [script], { input: '', encoding: 'utf8', env, timeout: 30000 });
      assert.equal(result.status, 1);
      assert.equal(result.stdout, '');
    } finally {
      try { unlinkSync(join(dataDir, 'current')); } catch { /* not created */ }
      rmSync(dataDir, { recursive: true, force: true });
      rmSync(store, { recursive: true, force: true });
    }
  });
