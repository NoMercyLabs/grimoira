// Cold start. Everything a fresh install needs before it is useful: build the CLI, create the store,
// discover and register the projects in the tree, index the code surface, absorb the AI-meta docs,
// and ingest every past conversation held about this repo.
//
// Runs at install time via the Setup hook, and is safe to re-run: every step upserts, so this doubles
// as the repair path after a rename or a restructure.
//
// Usage: node init.mjs [--root <dir>] [--instance <name>] [--skip-chat] [--quiet]
import { existsSync, readdirSync, statSync } from 'node:fs';
import { spawnSync } from 'node:child_process';
import { homedir } from 'node:os';
import { join, basename, dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const HERE = dirname(fileURLToPath(import.meta.url));
const arg = (flag, fallback = null) => {
  const i = process.argv.indexOf(flag);
  return i > -1 && process.argv[i + 1] && !process.argv[i + 1].startsWith('--') ? process.argv[i + 1] : fallback;
};
const quiet = process.argv.includes('--quiet');
const log = (...m) => { if (!quiet) console.log(...m); };
const step = (n, msg) => log(`\n[${n}] ${msg}`);

const root = resolve(arg('--root') || process.env.CLAUDE_PROJECT_DIR || process.cwd());
const instance = arg('--instance') || basename(root.replace(/[\\/]+$/, '')).toLowerCase().replace(/[^a-z0-9_-]/g, '');
const dbPath = join(homedir(), '.grimora', instance, 'grimora.db');

log(`grimora init`);
log(`  repo     ${root}`);
log(`  instance ${instance}`);
log(`  store    ${dbPath}`);

// ---------------------------------------------------------------- 1. the CLI
step(1, 'CLI');
const exe = join(HERE, 'bin-cli', 'grimora.exe');
const dll = join(HERE, 'bin-cli', 'grimora.dll');
if (!existsSync(exe) && !existsSync(dll)) {
  log('  building (build output is not committed)…');
  // bin-cli/ is the published Grimora.Cli, the thin client of the server (sub-card 29e); never grimora.cs.
  const b = spawnSync('dotnet', ['publish', join(HERE, 'src', 'Grimora.Cli', 'Grimora.Cli.csproj'), '-c', 'Release', '-o', join(HERE, 'bin-cli'), '-p:PublishAot=false'], {
    stdio: quiet ? 'ignore' : 'inherit',
    timeout: 600000,
  });
  if (b.status !== 0) {
    console.error('  FAILED: could not build the CLI. The .NET SDK is required for the store to exist at all.');
    process.exit(1);
  }
}
// The thin client starts bin-server/Grimora.Server on its first verb, so it has to exist too.
if (!existsSync(join(HERE, 'bin-server', 'Grimora.Server.dll'))) {
  log('  building the server…');
  const s = spawnSync('dotnet', ['publish', join(HERE, 'src', 'Grimora.Server', 'Grimora.Server.csproj'), '-c', 'Release', '-o', join(HERE, 'bin-server'), '-p:PublishAot=false'], {
    stdio: quiet ? 'ignore' : 'inherit',
    timeout: 600000,
  });
  if (s.status !== 0) {
    console.error('  FAILED: could not build the server. The CLI forwards every verb to it.');
    process.exit(1);
  }
}
const runCli = (args, timeout = 300000) => {
  const useExe = existsSync(exe);
  const r = spawnSync(useExe ? exe : 'dotnet', useExe ? args : [dll, ...args], {
    encoding: 'utf8', timeout, cwd: root,
  });
  const out = `${r.stdout || ''}${r.stderr || ''}`.trim();
  return { ok: r.status === 0, out };
};

const init = runCli(['init', '--instance', instance]);
log(`  ${init.out || 'store ready'}`);

// ------------------------------------------------------- 2. discover projects
step(2, 'projects');
const MARKERS = [
  { file: 'package.json', lang: 'ts', globs: '*.ts,*.vue' },
  { file: 'composer.json', lang: 'php', globs: '*.php' },
  { file: 'build.gradle.kts', lang: 'kotlin', globs: '*.kt' },
  { file: 'build.gradle', lang: 'kotlin', globs: '*.kt' },
  { file: 'settings.gradle.kts', lang: 'kotlin', globs: '*.kt' },
  { file: 'go.mod', lang: 'go', globs: '*.go' },
  { file: 'Cargo.toml', lang: 'rust', globs: '*.rs' },
  { file: 'pyproject.toml', lang: 'python', globs: '*.py' },
  // Native forks carry real product IP and have no manifest a package manager would recognise.
  { file: 'CMakeLists.txt', lang: 'c', globs: '*.h' },
  { file: 'configure', lang: 'c', globs: '*.h' },
  { file: 'meson.build', lang: 'c', globs: '*.h' },
];
const SKIP_DIR = new Set([
  'node_modules', '.git', 'dist', 'build', 'bin', 'obj', '.next', '.nuxt', '.gradle', '.idea',
  'vendor', 'coverage', '.venv', 'venv', '__pycache__', 'Pods', 'DerivedData', 'target', 'out',
  '.turbo', '.cache', 'worktrees', '.claude',
]);

function detect(dir) {
  for (const m of MARKERS) if (existsSync(join(dir, m.file))) return m;
  try {
    if (readdirSync(dir).some((f) => f.endsWith('.csproj') || f.endsWith('.sln')))
      return { lang: 'csharp', globs: '*.cs' };
  } catch { /* unreadable */ }
  return null;
}

// A repo root is rarely the interesting unit in a monorepo; the projects under it are. Depth is
// capped because a marker buried deeper than this is a fixture or an example, not a project.
const found = new Map();
(function scan(dir, depth) {
  if (depth > 3) return;
  const hit = detect(dir);
  if (hit && dir !== root) found.set(resolve(dir), hit);
  let entries;
  try { entries = readdirSync(dir, { withFileTypes: true }); } catch { return; }
  for (const e of entries) {
    if (!e.isDirectory() || SKIP_DIR.has(e.name) || e.name.startsWith('.')) continue;
    scan(join(dir, e.name), depth + 1);
  }
})(root, 0);
if (found.size === 0 && detect(root)) found.set(root, detect(root));

// A gradle submodule or a workspace package sitting inside an already-detected project is part of
// that project, not a peer of it. Registering both indexes the same files under two names and makes
// "which project owns this symbol" ambiguous, so only the outermost root of each tree survives.
const isInside = (child, parent) =>
  child !== parent && child.toLowerCase().startsWith(parent.toLowerCase().replace(/[\\/]*$/, '') + '\\');
for (const dir of [...found.keys()]) {
  if ([...found.keys()].some((other) => isInside(dir, other))) found.delete(dir);
}

// A project that no longer exists on disk keeps answering questions with paths that are gone, which
// is worse than not knowing. Registrations whose root has vanished are dropped along with their edges.
const existing = runCli(['projects', '--instance', instance]);
for (const line of (existing.out || '').split(/\r?\n/)) {
  const m = line.match(/^\s*(\S+)\s+\[[^\]]*\]\s+(.+?)\s+\(/);
  if (!m || existsSync(m[2])) continue;
  runCli(['forget-project', '--name', m[1], '--instance', instance], 30000);
  log(`  - ${m[1]} (root gone: ${m[2]})`);
}

const listed = runCli(['projects', '--instance', instance]);
const knownRoots = new Set(
  (listed.out || '').split(/\r?\n/)
    .map((l) => (l.match(/([A-Za-z]:[\\/][^\s|]+)/) || [])[1])
    .filter(Boolean)
    .map((p) => resolve(p).toLowerCase())
);

const takenNames = new Set(
  (listed.out || '').split(/\r?\n/)
    .map((l) => (l.match(/^\s*(\S+)\s+\[/) || [])[1])
    .filter(Boolean)
    .map((n) => n.toLowerCase())
);

let registered = 0;
for (const [dir, meta] of found) {
  if (knownRoots.has(dir.toLowerCase())) continue;
  if ([...knownRoots].some((known) => isInside(dir, known))) continue;
  // Registration upserts on the name, so a bare basename is dangerous: "android", "app", "shared"
  // and "site" all recur in a monorepo, and the second one silently rewrites the first one's root
  // and inherits its edges. Qualify with the parent directory until the name is actually unique.
  let name = basename(dir);
  if (takenNames.has(name.toLowerCase())) name = `${basename(dirname(dir))}-${name}`;
  if (takenNames.has(name.toLowerCase())) { log(`  ! skipped ${dir} (cannot derive a unique name)`); continue; }
  takenNames.add(name.toLowerCase());
  const r = runCli(['project', '--name', name, '--root', dir, '--instance', instance], 30000);
  if (r.ok) { registered++; log(`  + ${name.padEnd(28)} ${meta.lang}`); }
}
log(`  ${registered} newly registered, ${found.size - registered} already known`);

// --------------------------------------------------------------- 3. the code
step(3, 'code surface');
const code = spawnSync(process.execPath, [join(HERE, 'index-code.mjs'), '--instance', instance], {
  encoding: 'utf8', timeout: 900000, cwd: root,
});
log((code.stdout || code.stderr || '').split(/\r?\n/).filter((l) => l.trim() && !/Warning|trace-warn/.test(l)).map((l) => `  ${l.trim()}`).join('\n'));

// --------------------------------------------------------------- 4. packages
step(4, 'package identities');
log(`  ${runCli(['index-packages', '--root', root, '--instance', instance]).out || 'none'}`);

// ------------------------------------------------------------------- 5. docs
step(5, 'docs');
for (const rel of ['.claude', 'docs']) {
  const dir = join(root, rel);
  if (!existsSync(dir)) continue;
  log(`  ${rel}: ${runCli(['index-docs', '--from', dir, '--instance', instance], 600000).out || 'none'}`);
}

// ----------------------------------------------------------------- 6. memory
step(6, 'rules');
const memDir = join(homedir(), '.claude', 'projects', root.replace(/[:\\/]+/g, '-'), 'memory');
if (existsSync(memDir)) log(`  ${runCli(['index-memory', '--from', memDir, '--instance', instance]).out || 'none'}`);
else log('  no memory directory for this repo');

// ---------------------------------------------------- 7. past conversations
// Every context window ever opened against this repo is prior context, and sessions launched from a
// subdirectory land in their own transcript directory. All of them belong to this product's history,
// so the whole set is ingested — matched on a separator boundary so a sibling repo whose name merely
// starts the same (NoMercy vs NoMercyLabs) is not swept in.
step(7, 'past conversations');
if (process.argv.includes('--skip-chat')) {
  log('  skipped');
} else {
  const projectsDir = join(homedir(), '.claude', 'projects');
  const prefix = root.replace(/[:\\/]+/g, '-');
  let dirs = [];
  try {
    dirs = readdirSync(projectsDir).filter((d) => d === prefix || d.startsWith(`${prefix}-`));
  } catch { /* no transcripts */ }

  let sessions = 0;
  for (const d of dirs) {
    const full = join(projectsDir, d);
    try { sessions += readdirSync(full).filter((f) => f.endsWith('.jsonl')).length; } catch { /* skip */ }
  }
  log(`  ${dirs.length} transcript director(ies), ${sessions} session(s)`);
  for (const d of dirs) {
    const r = runCli(['index-chat', '--from', join(projectsDir, d), '--instance', instance], 900000);
    log(`    ${d}: ${r.out || 'nothing new'}`);
  }
}

// ---------------------------------------------------------------- 8. summary
step(8, 'ready');
log(runCli(['stats', '--instance', instance]).out);
