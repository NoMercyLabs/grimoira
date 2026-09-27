// Compat shim (slice 32c): AITM_* env vars still count for one release, and the old ~/.aitm store
// folder is moved once to ~/.grimora (backup first, old folder left as .aitm.moved-<stamp>).
import { existsSync, mkdirSync, readdirSync, renameSync, copyFileSync, statSync } from 'node:fs';
import { join } from 'node:path';

export function promoteLegacyEnv(env = process.env) {
  for (const [name, value] of Object.entries(env)) {
    if (!/^AITM_/i.test(name)) continue;
    const twin = `GRIMORA_${name.slice(5)}`;
    if (!env[twin]) env[twin] = value;
  }
}

function copyTree(from, to, rename) {
  mkdirSync(to, { recursive: true });
  for (const entry of readdirSync(from)) {
    const target = join(to, rename ? entry.replace(/aitm/gi, 'grimora') : entry);
    if (statSync(join(from, entry)).isDirectory()) copyTree(join(from, entry), target, rename);
    else copyFileSync(join(from, entry), target);
  }
}

export function moveLegacyStore(home, now = new Date()) {
  const oldDir = join(home, '.aitm');
  const newDir = join(home, '.grimora');
  if (!existsSync(oldDir) || existsSync(newDir)) return false;
  const stamp = now.toISOString().replace(/[-:]/g, '').replace('T', '-').slice(0, 15);
  copyTree(oldDir, join(home, `.aitm.backup-${stamp}`), false);
  copyTree(oldDir, `${newDir}.partial`, true);
  renameSync(`${newDir}.partial`, newDir);
  renameSync(oldDir, join(home, `.aitm.moved-${stamp}`));
  return true;
}
