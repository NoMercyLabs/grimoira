// The SessionStart step of an installed plugin: the published CLI and server are built into
// CLAUDE_PLUGIN_DATA on the first session after an install or a source change, in the background, and the
// hook runs through the built CLI once that build is current. Nothing here runs a real `dotnet publish`:
// the build is a fake that writes the files a publish would.
//
// Each build lands in its own folder, <data>/builds/<stamp>/{bin-cli,bin-server}, and <data>/current (a
// directory junction, a symlink off Windows) points at the newest complete one. A running server and a live
// hook hold the files of the build they started from, so a rebuild must never write into that folder.
import { existsSync, mkdirSync, mkdtempSync, readdirSync, readFileSync, realpathSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { spawn, spawnSync } from 'node:child_process';
import assert from 'node:assert/strict';
import { test } from 'node:test';
import { sessionStart, startDetached } from './session-start.mjs';
import { buildAndPoint } from './build-cli-and-server.mjs';
import { publishedCliDll } from './published-cli.mjs';

function fixture() {
  const dir = mkdtempSync(join(tmpdir(), 'grimora-session-start-'));
  const root = join(dir, 'checkout');
  const dataDir = join(dir, 'plugin-data');
  mkdirSync(join(root, 'src', 'Grimora.Cli'), { recursive: true });
  mkdirSync(join(root, 'src', 'Grimora.Server'), { recursive: true });
  writeFileSync(join(root, 'src', 'Grimora.Cli', 'Program.cs'), 'class Program {}');
  writeFileSync(join(root, 'src', 'Grimora.Server', 'Program.cs'), 'class Server {}');
  writeFileSync(join(root, 'Directory.Build.props'), '<Project />');
  mkdirSync(dataDir, { recursive: true });
  return { dir, root, dataDir, clean: () => rmSync(dir, { recursive: true, force: true }) };
}

// Stands in for `dotnet publish <project> -o <out>`: writes the entry file each publish produces.
function fakePublish(calls = [], onPublish = () => {}) {
  return (project, out) => {
    onPublish(project, out);
    calls.push({ project, out });
    mkdirSync(out, { recursive: true });
    writeFileSync(join(out, project.includes('Grimora.Server') ? 'Grimora.Server.dll' : 'grimora.dll'), 'built');
  };
}

function run(f, overrides = {}) {
  const calls = { hook: [], hookEnv: [], build: [], lines: [] };
  const code = sessionStart({
    root: f.root,
    dataDir: f.dataDir,
    pluginData: true,
    runHook: (cli, env) => { calls.hook.push(cli); calls.hookEnv.push(env); return overrides.hookExit ?? 0; },
    startBuild: dataDir => calls.build.push(dataDir),
    write: line => calls.lines.push(line),
    ...overrides.deps,
  });
  return { code, calls };
}

const build = (f, publish = fakePublish()) => buildAndPoint({ root: f.root, dataDir: f.dataDir, publish });
const current = f => { try { return realpathSync(join(f.dataDir, 'current')); } catch { return null; } };
const builds = f => readdirSync(join(f.dataDir, 'builds')).sort();
const changeSource = (f, text) => writeFileSync(join(f.root, 'src', 'Grimora.Server', 'Program.cs'), text);

// Keeps a folder in use the way a running server does: a process whose working directory is inside it.
function holdFolder(folder) {
  const child = spawn(process.execPath, ['-e', 'setTimeout(() => {}, 60000)'], { cwd: folder, stdio: 'ignore' });
  spawnSync(process.execPath, ['-e', 'setTimeout(() => {}, 300)']); // let it start
  return () => { child.kill(); spawnSync(process.execPath, ['-e', 'setTimeout(() => {}, 300)']); };
}

// Only Windows refuses to rename a folder in use; elsewhere a running process keeps its files after a delete.
const windowsOnly = { skip: process.platform !== 'win32' && 'a folder in use only blocks a rename on Windows' };

test('a fresh data folder starts one build, prints one line and exits 0 without running the hook', () => {
  const f = fixture();
  try {
    const { code, calls } = run(f);
    assert.equal(code, 0);
    assert.deepEqual(calls.build, [f.dataDir]);
    assert.equal(calls.hook.length, 0);
    assert.equal(calls.lines.length, 1);
    assert.match(calls.lines[0], /Grimora is building/);
  } finally { f.clean(); }
});

// Found by the end-to-end run: with no data folder yet the lock file could not be created, which read as
// "another session is building", so nothing ever built.
test('a data folder that does not exist yet is created and the build starts', () => {
  const f = fixture();
  try {
    rmSync(f.dataDir, { recursive: true, force: true });
    const { code, calls } = run(f);
    assert.equal(code, 0);
    assert.deepEqual(calls.build, [f.dataDir]);
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
    assert.match(second.calls.lines[0], /Grimora is building/);
  } finally { f.clean(); }
});

test('a finished build is current: no build, the hook runs through current/bin-cli and its exit code comes back', () => {
  const f = fixture();
  try {
    run(f);
    build(f);
    const { code, calls } = run(f, { hookExit: 3 });
    assert.equal(calls.build.length, 0);
    assert.deepEqual(calls.hook, [join(f.dataDir, 'current', 'bin-cli', 'grimora.dll')]);
    assert.equal(code, 3);
  } finally { f.clean(); }
});

test('the hook gets the plugin root, so a server it starts can find the plugin files', () => {
  const f = fixture();
  try {
    run(f);
    build(f);
    const { calls } = run(f);
    assert.equal(calls.hookEnv[0].Grimora_PLUGIN_ROOT, f.root);
  } finally { f.clean(); }
});

// A server started without the hook (the logon task, a thin client of another slot) has no Grimora_PLUGIN_ROOT,
// so every SessionStart also records the plugin root in the data folder, where the server finds it.
test('every SessionStart records the plugin root in the data folder, building or not', () => {
  const f = fixture();
  try {
    const rootFile = join(f.dataDir, 'plugin-root.txt');
    run(f);
    assert.equal(readFileSync(rootFile, 'utf8'), f.root);
    build(f);
    writeFileSync(rootFile, 'an old plugin version folder');
    run(f);
    assert.equal(readFileSync(rootFile, 'utf8'), f.root);
    assert.deepEqual(readdirSync(f.dataDir).filter(name => name.startsWith('plugin-root') && name !== 'plugin-root.txt'), [],
      'the temp file of the atomic write is left behind');
  } finally { f.clean(); }
});

test('a checkout run writes no plugin-root file', () => {
  const f = fixture();
  try {
    run({ ...f, dataDir: f.root }, { deps: { pluginData: false } });
    assert.ok(!existsSync(join(f.root, 'plugin-root.txt')));
  } finally { f.clean(); }
});

test('a changed source makes the build stale, so SessionStart starts a rebuild', () => {
  const f = fixture();
  try {
    run(f);
    build(f);
    changeSource(f, 'class Server { void NewTool() {} }');
    const { calls } = run(f);
    assert.deepEqual(calls.build, [f.dataDir]);
    assert.equal(calls.hook.length, 0);
  } finally { f.clean(); }
});

test('a changed shared build file (Directory.Build.props) also makes the build stale', () => {
  const f = fixture();
  try {
    run(f);
    build(f);
    writeFileSync(join(f.root, 'Directory.Build.props'), '<Project><PropertyGroup /></Project>');
    assert.equal(run(f).calls.build.length, 1);
  } finally { f.clean(); }
});

test('build output under src (obj/, bin/) is not a source change, so a publish never makes its own build stale', () => {
  const f = fixture();
  try {
    run(f);
    build(f);
    mkdirSync(join(f.root, 'src', 'Grimora.Cli', 'obj'), { recursive: true });
    writeFileSync(join(f.root, 'src', 'Grimora.Cli', 'obj', 'project.assets.json'), '{}');
    mkdirSync(join(f.root, 'src', 'Grimora.Cli', 'bin'), { recursive: true });
    writeFileSync(join(f.root, 'src', 'Grimora.Cli', 'bin', 'grimora.dll'), 'x');
    const { calls } = run(f);
    assert.equal(calls.build.length, 0);
    assert.equal(calls.hook.length, 1);
  } finally { f.clean(); }
});

test('the build publishes the CLI and the server as siblings in one build folder, then frees the lock', () => {
  const f = fixture();
  try {
    run(f);
    const publishes = [];
    build(f, fakePublish(publishes));
    assert.deepEqual(publishes.map(p => p.project), [
      join(f.root, 'src', 'Grimora.Cli', 'Grimora.Cli.csproj'),
      join(f.root, 'src', 'Grimora.Server', 'Grimora.Server.csproj'),
    ]);
    const [only] = builds(f);
    assert.deepEqual(publishes.map(p => p.out), [
      join(f.dataDir, 'builds', only, 'bin-cli'),
      join(f.dataDir, 'builds', only, 'bin-server'),
    ]);
    assert.ok(existsSync(join(f.dataDir, 'current', 'bin-server', 'Grimora.Server.dll')));
    assert.equal(run(f).calls.build.length, 0, 'the lock is free and the build is current');
  } finally { f.clean(); }
});

test('current never points at a half-built folder: it moves only after both publishes and the stamp', () => {
  const f = fixture();
  try {
    const seen = [];
    build(f, fakePublish([], () => seen.push(current(f))));
    assert.deepEqual(seen, [null, null], 'a first build has no current until it is complete');
    const first = current(f);
    changeSource(f, 'class Server { int v = 2; }');
    const seenOnRebuild = [];
    build(f, fakePublish([], () => seenOnRebuild.push(current(f))));
    assert.deepEqual(seenOnRebuild, [first, first]);
    assert.notEqual(current(f), first);
    assert.equal(readFileSync(join(f.dataDir, 'current', 'bin-cli', 'build-stamp.txt'), 'utf8').length, 64);
  } finally { f.clean(); }
});

test('a failed publish leaves current on the last complete build and frees the lock', () => {
  const f = fixture();
  try {
    build(f);
    const first = current(f);
    changeSource(f, 'class Server { int v = 3; }');
    const failOnServer = (project, out) => {
      if (project.includes('Grimora.Server')) throw new Error('publish failed');
      fakePublish()(project, out);
    };
    assert.throws(() => build(f, failOnServer), /publish failed/);
    assert.equal(current(f), first);
    assert.equal(run(f).calls.build.length, 1, 'the next session tries again');
  } finally { f.clean(); }
});

test('a rebuild while a server runs from the old folder writes only the new folder', windowsOnly, () => {
  const f = fixture();
  try {
    build(f);
    const oldFolder = current(f);
    const before = readdirSync(oldFolder, { recursive: true }).sort();
    const beforeServer = readFileSync(join(oldFolder, 'bin-server', 'Grimora.Server.dll'), 'utf8');
    const release = holdFolder(join(f.dataDir, 'current', 'bin-server'));
    try {
      changeSource(f, 'class Server { int v = 4; }');
      const outs = [];
      build(f, fakePublish(outs));
      assert.ok(outs.every(p => !p.out.startsWith(oldFolder)), 'a publish wrote into the folder in use');
      assert.notEqual(current(f), oldFolder);
      assert.deepEqual(readdirSync(oldFolder, { recursive: true }).sort(), before);
      assert.equal(readFileSync(join(oldFolder, 'bin-server', 'Grimora.Server.dll'), 'utf8'), beforeServer);
    } finally { release(); }
  } finally { f.clean(); }
});

test('the previous build is kept; an older one is deleted only when nothing holds it', windowsOnly, () => {
  const f = fixture();
  try {
    build(f);
    const v1 = current(f);
    const release = holdFolder(join(v1, 'bin-server'));
    try {
      changeSource(f, 'v2'); build(f);
      const v2 = current(f);
      changeSource(f, 'v3'); build(f);
      assert.ok(existsSync(v2), 'the previous build is the rollback and stays');
      assert.ok(existsSync(join(v1, 'bin-server', 'Grimora.Server.dll')), 'an older build in use was deleted');
    } finally { release(); }
    changeSource(f, 'v4'); build(f);
    assert.ok(!existsSync(v1), 'an older build nothing holds is deleted');
    assert.equal(builds(f).length, 2, `current and previous only, got ${builds(f).join(', ')}`);
  } finally { f.clean(); }
});

test('with no plugin data folder the checkout build runs the hook without a stamp check', () => {
  const f = fixture();
  try {
    mkdirSync(join(f.root, 'bin-cli'));
    writeFileSync(join(f.root, 'bin-cli', 'grimora.dll'), 'built by build-cli.ps1');
    mkdirSync(join(f.root, 'bin-server'));
    writeFileSync(join(f.root, 'bin-server', 'Grimora.Server.dll'), 'built by build-server.ps1');
    const { calls } = run({ ...f, dataDir: f.root }, { deps: { pluginData: false } });
    assert.equal(calls.build.length, 0);
    assert.deepEqual(calls.hook, [join(f.root, 'bin-cli', 'grimora.dll')]);
  } finally { f.clean(); }
});

test('the published CLI is the data folder\'s current build, else the checkout\'s bin-cli', () => {
  assert.equal(publishedCliDll({ CLAUDE_PLUGIN_DATA: join('d', 'data') }, join('c', 'checkout')),
    join('d', 'data', 'current', 'bin-cli', 'grimora.dll'));
  assert.equal(publishedCliDll({}, join('c', 'checkout')), join('c', 'checkout', 'bin-cli', 'grimora.dll'));
});

// Slice 29d: a server started with the hook's stdout/stderr pipes kept them open, and the hook runner
// waited 380 s for end-of-stream. The build must not hold them: a parent whose pipes are read to the end
// returns while the detached child is still running.
test('the detached build does not hold the SessionStart pipes open', () => {
  const dir = mkdtempSync(join(tmpdir(), 'grimora-detached-'));
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
