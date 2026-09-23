// NoMercy pilot: record newly launched children. Never adopt or kill existing PIDs.
import { mkdirSync, writeFileSync, renameSync, realpathSync, readdirSync, readFileSync, unlinkSync, lstatSync } from 'node:fs';
import { resolve, relative, isAbsolute, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { randomUUID } from 'node:crypto';
import { spawn } from 'node:child_process';

export function pilotRoot(project, workspace) {
  try {
    const root = realpathSync(workspace);
    const child = realpathSync(project);
    const rel = relative(root, child);
    return rel === '' || (!rel.startsWith('..') && !isAbsolute(rel)) ? root : null;
  } catch { return null; }
}

export function trimCompleted(root, keep = 50) {
  const directory = join(root, '.scratch', 'process-owners');
  const completed = [];
  try {
    const containment = relative(realpathSync(root), realpathSync(directory));
    if (containment.startsWith('..') || isAbsolute(containment)) return;
    // Bound housekeeping work. Running and ambiguous records are never removed.
    for (const name of readdirSync(directory).slice(0, 1000)) {
      if (!/^[a-f0-9-]{36}\.json$/.test(name)) continue;
      const filename = join(directory, name);
      try {
        if (!lstatSync(filename).isFile() || lstatSync(filename).isSymbolicLink()) continue;
        const record = JSON.parse(readFileSync(filename, 'utf8'));
        if (record.version === 1 && ['exited', 'failed'].includes(record.state) && record.ended_at) {
          completed.push({ filename, time: record.ended_at });
        }
      } catch { /* Keep unreadable records for inspection. */ }
    }
    completed.sort((a, b) => b.time.localeCompare(a.time));
    for (const record of completed.slice(keep)) unlinkSync(record.filename);
  } catch { /* Retention never prevents launching or stopping a child. */ }
}

export function launchOwned(command, args, { root, cwd, stdio = 'inherit', onWarning = () => {} }) {
  const id = randomUUID();
  const directory = join(root, '.scratch', 'process-owners');
  const filename = join(directory, `${id}.json`);
  const record = {
    version: 1, id, workspace: root, cwd: resolve(cwd),
    owner: { pid: process.pid, parent_pid: process.ppid, started_at: new Date(Date.now() - process.uptime() * 1000).toISOString() },
    // MCP startup does not supply a reliable application session ID. Do not invent one.
    application_session: null, capability: 'aitm-tool-server',
    launched_at: new Date().toISOString(), child_pid: null, state: 'starting',
    cancellation: 'Only the live owning launcher may signal its original child handle.',
  };
  const save = () => {
    try {
      mkdirSync(directory, { recursive: true });
      const temp = `${filename}.tmp`;
      writeFileSync(temp, JSON.stringify(record, null, 2) + '\n', { mode: 0o600 });
      renameSync(temp, filename);
    } catch { onWarning('process ownership record unavailable'); }
  };
  save();
  const child = spawn(command, args, { cwd, stdio });
  child.once('spawn', () => {
    record.child_pid = child.pid;
    record.state = 'running';
    save();
  });
  child.once('error', () => {
    record.state = 'failed';
    record.ended_at = new Date().toISOString();
    save();
    trimCompleted(root);
  });
  child.once('exit', (code, signal) => {
    record.state = 'exited';
    record.exit_code = code;
    record.signal = signal;
    record.ended_at = new Date().toISOString();
    save();
    trimCompleted(root);
  });
  return {
    child, id, filename,
    cancel(signal = 'SIGTERM') {
      // No PID lookup, tree walk, or persisted record is used as kill authority.
      if (child.exitCode !== null || child.signalCode !== null || !child.pid) return false;
      return child.kill(signal);
    },
  };
}

// Inspection only. A running record after a crash is not proof of a live owner.
export function inspectOwners(root, limit = 100) {
  const directory = join(root, '.scratch', 'process-owners');
  let files;
  try { files = readdirSync(directory).filter(name => /^[a-f0-9-]{36}\.json$/.test(name)); }
  catch (error) { if (error.code === 'ENOENT') return { records: [], limited: false }; throw error; }
  return { limited: files.length > limit, records: files.slice(0, limit).map(name => {
    try {
      const record = JSON.parse(readFileSync(join(directory, name), 'utf8'));
      return { id: record.id, owner_pid: record.owner?.pid, child_pid: record.child_pid,
        state: record.state, launched_at: record.launched_at,
        liveness: 'not verified; record is not permission to terminate a PID' };
    } catch { return { file: name, state: 'unreadable' }; }
  }) };
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  if (process.argv[2] !== 'status') {
    process.stderr.write('Usage: node process-owner.mjs status [NoMercy workspace path]\n');
    process.exitCode = 2;
  } else {
    const root = resolve(process.argv[3] || join(import.meta.dirname, '..', 'NoMercy'));
    process.stdout.write(JSON.stringify(inspectOwners(root), null, 2) + '\n');
  }
}
