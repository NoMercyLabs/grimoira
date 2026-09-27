// The UserPromptSubmit, PreCompact and SessionEnd slots run through run-hook.mjs. During the first build there
// is no <data>/current yet, and a direct `dotnet <missing dll>` printed dotnet's "Could not execute" block to
// stdout and exited 1 on every prompt. Not ready: nothing printed, exit 0, and never a build (only SessionStart
// builds). Ready: the CLI's `hook <event>` runs with stdin, stdout and the exit code passed straight through.
import { existsSync, mkdtempSync, readdirSync, rmSync, symlinkSync, unlinkSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { spawnSync } from 'node:child_process';
import assert from 'node:assert/strict';
import { test } from 'node:test';

const script = join(import.meta.dirname, 'run-hook.mjs');
const events = ['UserPromptSubmit', 'PreCompact', 'SessionEnd'];
const payload = JSON.stringify({ session_id: 'run-hook-test', cwd: join(tmpdir(), 'no-such-project'), prompt: 'hi' });

function isolated(dataDir) {
  // Never the live service: a throwaway store has its own pipe. A hook starts the service on demand; it
  // holds the store's files, so a test stops it before deleting them, and it also ends itself after 3 idle seconds.
  const store = mkdtempSync(join(tmpdir(), 'grimora-run-hook-store-'));
  return { store, env: { ...process.env, CLAUDE_PLUGIN_DATA: dataDir, Grimora_DATA_DIR: store, Grimora_IDLE_SECONDS: '3' } };
}

for (const event of events) {
  test(`${event}: no current build prints nothing, exits 0 and starts no build`, () => {
    const dataDir = mkdtempSync(join(tmpdir(), 'grimora-run-hook-'));
    const { store, env } = isolated(dataDir);
    try {
      const result = spawnSync(process.execPath, [script, event], { input: payload, encoding: 'utf8', env, timeout: 20000 });
      assert.equal(result.status, 0);
      assert.equal(result.stdout, '');
      assert.equal(result.stderr, '');
      assert.deepEqual(readdirSync(dataDir), [], 'a hook slot other than SessionStart started a build');
    } finally {
      rmSync(dataDir, { recursive: true, force: true });
      rmSync(store, { recursive: true, force: true });
    }
  });
}

// The checkout's own bin-cli (build-cli.ps1) stands in for a finished build: <data>/current points at the checkout.
const built = existsSync(join(import.meta.dirname, 'bin-cli', 'grimora.dll'));
for (const event of events) {
  test(`${event}: with a current build the slot's stdout and exit code equal the direct CLI call`,
    { skip: !built && 'needs bin-cli/ from build-cli.ps1' }, () => {
      const dataDir = mkdtempSync(join(tmpdir(), 'grimora-run-hook-'));
      const { store, env } = isolated(dataDir);
      try {
        symlinkSync(import.meta.dirname, join(dataDir, 'current'), process.platform === 'win32' ? 'junction' : 'dir');
        const direct = spawnSync('dotnet', [join(dataDir, 'current', 'bin-cli', 'grimora.dll'), 'hook', event],
          { input: payload, encoding: 'utf8', env, timeout: 30000 });
        const slot = spawnSync(process.execPath, [script, event], { input: payload, encoding: 'utf8', env, timeout: 30000 });
        assert.equal(slot.stdout, direct.stdout);
        assert.equal(slot.status, direct.status);
        assert.deepEqual(readdirSync(dataDir), ['current'], 'a hook slot other than SessionStart started a build');
      } finally {
        // unlink removes only the link; a recursive delete must never reach the checkout behind it.
        try { unlinkSync(join(dataDir, 'current')); } catch { /* not created */ }
        spawnSync('dotnet', [join(import.meta.dirname, 'bin-cli', 'grimora.dll'), 'service', 'stop'], { env, timeout: 30000 });
        rmSync(dataDir, { recursive: true, force: true });
        rmSync(store, { recursive: true, force: true, maxRetries: 10, retryDelay: 300 });
      }
    });
}
