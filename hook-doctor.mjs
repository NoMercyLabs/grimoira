#!/usr/bin/env node
// Check Claude hook registrations without running hooks or printing their commands.
// An enabled plugin plus matching direct hooks runs the same AITM script twice.
import { readFileSync } from 'node:fs';
import { homedir } from 'node:os';
import { join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const key = (event, script) => `${event}:${script}`;
const scripts = value => String(value || '').match(/[a-z0-9-]+\.mjs\b/gi) || [];

function hooks(data, directOnly) {
  const found = [];
  for (const [event, groups] of Object.entries(data?.hooks || {})) {
    for (const group of groups || []) {
      for (const hook of group.hooks || []) {
        const command = String(hook.command || '');
        if (directOnly && !/(^|[/\\])aitm[/\\]/i.test(command)) continue;
        for (const script of scripts([command, ...(hook.args || [])].join(' '))) {
          found.push(key(event, script.toLowerCase()));
        }
      }
    }
  }
  return found;
}

export function auditHooks(user, project, plugin) {
  const enabled = { ...(user?.enabledPlugins || {}), ...(project?.enabledPlugins || {}) };
  const pluginEnabledSetting = Object.entries(enabled).some(([name, value]) => /^aitm@/i.test(name) && value === true);
  const direct = [...hooks(user, true), ...hooks(project, true)];
  const pluginSet = new Set(hooks(plugin, false));
  const directCounts = new Map();
  for (const name of direct) directCounts.set(name, (directCounts.get(name) || 0) + 1);
  const directSet = new Set(direct);
  const overlap = [...pluginSet].filter(name => directSet.has(name)).sort();
  const directOnly = [...directSet].filter(name => !pluginSet.has(name)).sort();
  const directDuplicates = [...directCounts].filter(([, count]) => count > 1).map(([name]) => name).sort();
  return {
    pluginEnabledSetting,
    pluginHooks: pluginSet.size,
    directHooks: directSet.size,
    overlap,
    directOnly,
    directDuplicates,
    unsafe: directDuplicates.length > 0 || (pluginEnabledSetting && overlap.length > 0),
  };
}

function readJson(path) {
  try { return JSON.parse(readFileSync(path, 'utf8')); }
  catch (error) {
    if (error.code === 'ENOENT') return {};
    throw new Error(`Cannot read hook settings at ${path}: ${error.message}`);
  }
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const root = process.env.CLAUDE_CONFIG_DIR || join(homedir(), '.claude');
  try {
    const projectArg = process.argv.indexOf('--project');
    if (projectArg >= 0 && !process.argv[projectArg + 1]) throw new Error('--project requires a directory');
    const project = projectArg >= 0 ? process.argv[projectArg + 1] : process.env.CLAUDE_PROJECT_DIR || process.cwd();
    const result = auditHooks(
      readJson(join(root, 'settings.json')),
      readJson(join(project, '.claude', 'settings.json')),
      readJson(join(import.meta.dirname, '.claude-plugin', 'plugin.json')),
    );
    if (process.argv.includes('--json')) console.log(JSON.stringify(result));
    else console.log(`AITM hooks: ${result.pluginHooks} in plugin, ${result.directHooks} direct, ${result.overlap.length} overlapping, ${result.directOnly.length} direct only. Plugin enabled in settings: ${result.pluginEnabledSetting}. ${result.unsafe ? 'FAIL: duplicate hook launches possible.' : 'No duplicate indicated by settings.'}`);
    process.exitCode = result.unsafe ? 2 : 0;
  } catch (error) {
    console.error(error.message);
    process.exitCode = 1;
  }
}
