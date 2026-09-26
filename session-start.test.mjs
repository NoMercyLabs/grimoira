// The SessionStart step of an installed plugin: the published CLI and server are built into
// CLAUDE_PLUGIN_DATA on the first session after an install or a source change, in the background, and the
// hook runs through the built CLI once that build is current. Nothing here runs a real `dotnet publish`:
// the build is a fake that writes the files a publish would.
import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { spawnSync } from 'node:child_process';
import assert from 'node:assert/strict';
import { test } from 'node:test';
import { sessionStart, startDetached } from './session-start.mjs';
import { buildAndSwap } from './build-cli-and-server.mjs';

function fixture() {
  const dir = mkdtempSync(join(tmpdir(), 'aitm-session-start-'));
  const root = join(dir, 'checkout');
  const dataDir = join(dir, 'plugin-data');
  mkdirSync(join(root, 'src', 'Aitm.Cli'), { recursive: true });
  mkdirSync(join(root, 'src', 'Aitm.Server'), { recursive: true });
  writeFileSync(join(root, 'src', 'Aitm.Cli', 'Program.cs'), 'class Program {}');
  writeFileSync(join(root, 'src', 'Aitm.Server', 'Program.cs'), 'class Server {}');
  writeFileSync(join(root, 'Directory.Build.props'), '<Project />');
  mkdirSync(dataDir, { recursive: true });
  return { dir, root, dataDir, clean: () => rmSync(dir, { recursive: true, force: true }) };
}

// Stands in for `dotnet publish <project> -o <out>`: writes the entry file each publish produces.
function fakePublish(calls = []) {
  return (project, out) => {
    calls.push({ project, out });
    mkdirSync(out, { recursive: true });
    writeFileSync(join(out, project.includes('Aitm.Server') ? 'Aitm.Server.dll' : 'aitm.dll'), 'built');
  };
}

function run(f, overrides = {}) {
  const calls = { hook: [], build: [], lines: [] };
  const code = sessionStart({
    root: f.root,
    dataDir: f.dataDir,
    checkStamp: true,
    runHook: cli => { calls.hook.push(cli); return overrides.hookExit ?? 0; },
    startBuild: dataDir => calls.build.push(dataDir),
    write: line => calls.lines.push(line),
    ...overrides.deps,
  });
  return { code, calls };
}

test('a fresh data folder starts one build, prints one line and exits 0 without running the hook', () => {
  const f = fixture();
  try {
    const { code, calls } = run(f);
    assert.equal(code, 0);
    assert.deepEqual(calls.build, [f.dataDir]);
    assert.equal(calls.hook.length, 0);
    assert.equal(calls.lines.length, 1);
    assert.match(calls.lines[0], /AITM is building/);
  } finally { f.clean(); }
});

test('a second SessionStart while the first build runs starts no second build', () => {
  const f = fixture();
  try {
    const first = run(f);
    const second = run(f);
    assert.equal(first.calls.build.length, 1);
    assert.equal(second.calls.build.length, 0);
    assert.equal(second.code, 0);
    assert.match(second.calls.lines[0], /AITM is building/);
  } finally { f.clean(); }
});

test('a finished build is current: no build, the hook runs through the built CLI and its exit code comes back', () => {
  const f = fixture();
  try {
    run(f);
    buildAndSwap({ root: f.root, dataDir: f.dataDir, publish: fakePublish() });
    const { code, calls } = run(f, { hookExit: 3 });
    assert.equal(calls.build.length, 0);
    assert.deepEqual(calls.hook, [join(f.dataDir, 'bin-cli', 'aitm.dll')]);
    assert.equal(code, 3);
  } finally { f.clean(); }
});

test('a changed source makes the build stale, so SessionStart starts a rebuild', () => {
  const f = fixture();
  try {
    run(f);
    buildAndSwap({ root: f.root, dataDir: f.dataDir, publish: fakePublish() });
    writeFileSync(join(f.root, 'src', 'Aitm.Server', 'Program.cs'), 'class Server { void NewTool() {} }');
    const { calls } = run(f);
    assert.deepEqual(calls.build, [f.dataDir]);
    assert.equal(calls.hook.length, 0);
  } finally { f.clean(); }
});

test('a changed shared build file (Directory.Build.props) also makes the build stale', () => {
  const f = fixture();
  try {
    run(f);
    buildAndSwap({ root: f.root, dataDir: f.dataDir, publish: fakePublish() });
    writeFileSync(join(f.root, 'Directory.Build.props'), '<Project><PropertyGroup /></Project>');
    assert.equal(run(f).calls.build.length, 1);
  } finally { f.clean(); }
});

test('build output under src (obj/, bin/) is not a source change, so a publish never makes its own build stale', () => {
  const f = fixture();
  try {
    run(f);
    buildAndSwap({ root: f.root, dataDir: f.dataDir, publish: fakePublish() });
    mkdirSync(join(f.root, 'src', 'Aitm.Cli', 'obj'), { recursive: true });
    writeFileSync(join(f.root, 'src', 'Aitm.Cli', 'obj', 'project.assets.json'), '{}');
    mkdirSync(join(f.root, 'src', 'Aitm.Cli', 'bin'), { recursive: true });
    writeFileSync(join(f.root, 'src', 'Aitm.Cli', 'bin', 'aitm.dll'), 'x');
    const { calls } = run(f);
    assert.equal(calls.build.length, 0);
    assert.equal(calls.hook.length, 1);
  } finally { f.clean(); }
});

test('the build publishes the CLI and the server as siblings, then frees the lock', () => {
  const f = fixture();
  try {
    run(f);
    const publishes = [];
    writeFileSync(join(f.dataDir, 'marker'), '');
    mkdirSync(join(f.dataDir, 'bin-cli'));
    writeFileSync(join(f.dataDir, 'bin-cli', 'from-the-old-build.dll'), 'old');
    buildAndSwap({ root: f.root, dataDir: f.dataDir, publish: fakePublish(publishes) });
    assert.deepEqual(publishes.map(p => p.project), [
      join(f.root, 'src', 'Aitm.Cli', 'Aitm.Cli.csproj'),
      join(f.root, 'src', 'Aitm.Server', 'Aitm.Server.csproj'),
    ]);
    assert.ok(existsSync(join(f.dataDir, 'bin-cli', 'aitm.dll')));
    assert.ok(existsSync(join(f.dataDir, 'bin-server', 'Aitm.Server.dll')));
    assert.ok(!existsSync(join(f.dataDir, 'bin-cli', 'from-the-old-build.dll')), 'the old build is swapped out, not merged');
    assert.equal(run(f).calls.build.length, 0, 'the lock is free and the build is current');
  } finally { f.clean(); }
});

test('a failed publish swaps nothing in and frees the lock, so the next session tries again', () => {
  const f = fixture();
  try {
    run(f);
    const failing = () => { throw new Error('publish failed'); };
    assert.throws(() => buildAndSwap({ root: f.root, dataDir: f.dataDir, publish: failing }), /publish failed/);
    assert.ok(!existsSync(join(f.dataDir, 'bin-cli')));
    assert.equal(run(f).calls.build.length, 1);
  } finally { f.clean(); }
});

test('a finished build that could not be swapped in is swapped next time without publishing again', () => {
  const f = fixture();
  try {
    run(f);
    buildAndSwap({ root: f.root, dataDir: f.dataDir, publish: fakePublish() });
    // Recreate what a swap blocked by a running server leaves behind: a stamped build-next/.
    writeFileSync(join(f.root, 'src', 'Aitm.Cli', 'Program.cs'), 'class Program { int v = 2; }');
    const publishes = [];
    const blocked = (project, out) => {
      fakePublish(publishes)(project, out);
      if (project.includes('Aitm.Server')) writeFileSync(join(out, 'Aitm.Server.dll'), 'v2');
    };
    assert.throws(() => buildAndSwap({ root: f.root, dataDir: f.dataDir, publish: blocked, swap: () => { throw new Error('in use'); } }), /in use/);
    assert.equal(publishes.length, 2);
    buildAndSwap({ root: f.root, dataDir: f.dataDir, publish: () => { throw new Error('must not publish again'); } });
    assert.equal(readFileSync(join(f.dataDir, 'bin-server', 'Aitm.Server.dll'), 'utf8'), 'v2');
  } finally { f.clean(); }
});

test('with no plugin data folder the checkout build runs the hook without a stamp check', () => {
  const f = fixture();
  try {
    mkdirSync(join(f.root, 'bin-cli'));
    writeFileSync(join(f.root, 'bin-cli', 'aitm.dll'), 'built by build-cli.ps1');
    mkdirSync(join(f.root, 'bin-server'));
    writeFileSync(join(f.root, 'bin-server', 'Aitm.Server.dll'), 'built by build-server.ps1');
    const { calls } = run({ ...f, dataDir: f.root }, { deps: { checkStamp: false } });
    assert.equal(calls.build.length, 0);
    assert.deepEqual(calls.hook, [join(f.root, 'bin-cli', 'aitm.dll')]);
  } finally { f.clean(); }
});

// Slice 29d: a server started with the hook's stdout/stderr pipes kept them open, and the hook runner
// waited 380 s for end-of-stream. The build must not hold them: a parent whose pipes are read to the end
// returns while the detached child is still running.
test('the detached build does not hold the SessionStart pipes open', () => {
  const dir = mkdtempSync(join(tmpdir(), 'aitm-detached-'));
  try {
    const started = join(dir, 'started');
    const sleeper = join(dir, 'sleeper.mjs');
    writeFileSync(sleeper, `import { writeFileSync } from 'node:fs';
writeFileSync(${JSON.stringify(started)}, 'yes');
process.stdout.write('child output');
setTimeout(() => {}, 8000);`);
    const lib = new URL('./session-start.mjs', import.meta.url).href;
    const t0 = Date.now();
    const parent = spawnSync(process.execPath,
      ['--input-type=module', '-e', `const m = await import(${JSON.stringify(lib)}); m.startDetached(${JSON.stringify(sleeper)}, []);`],
      { encoding: 'utf8', timeout: 20000 });
    const elapsed = Date.now() - t0;
    assert.equal(parent.status, 0, parent.stderr);
    assert.ok(elapsed < 4000, `the parent's pipes closed after ${elapsed} ms; the child holds them`);
    const until = Date.now() + 5000;
    while (!existsSync(started) && Date.now() < until) spawnSync(process.execPath, ['-e', 'setTimeout(()=>{},100)']);
    assert.ok(existsSync(started), 'the detached child never started');
    assert.equal(parent.stdout, '');
  } finally { rmSync(dir, { recursive: true, force: true }); }
});
