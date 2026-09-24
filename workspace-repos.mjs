// Find a NoMercy repo by name through the workspace registry (.claude/scripts/repos.sh), never by a
// hard-coded path. The workspace moves repos between folders (2026-09-24: nomercy-tv went from apps/ to
// saas/), and every fixed path in a tool outside the workspace broke silently when it did.
import { readFileSync, existsSync } from 'node:fs';
import { join } from 'node:path';

export const WORKSPACE = process.env.NOMERCY_WORKSPACE || 'C:/Projects/NoMercy';

// Every registered repo's workspace-relative path, in registry order.
export function registeredRepos(workspace = process.env.NOMERCY_WORKSPACE || WORKSPACE) {
  const registry = join(workspace, '.claude', 'scripts', 'repos.sh');
  if (!existsSync(registry)) return [];
  return [...readFileSync(registry, 'utf8').matchAll(/^\s*"([^|"]+)\|/gm)].map((match) => match[1]);
}

// The folder of the registered repo whose last path segment is `name`, or null when none is registered.
export function repoPath(name, workspace = process.env.NOMERCY_WORKSPACE || WORKSPACE) {
  const path = registeredRepos(workspace).find((repo) => repo.split('/').pop() === name);
  return path ? join(workspace, ...path.split('/')) : null;
}
