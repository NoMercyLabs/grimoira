// The organisation is not only its repos. Hosts, domains, registries and the services that run on
// them are just as often what a question is about, and none of them live in a git remote — so a store
// built purely from code and repos still cannot answer "where does that run" without a file or a guess.
//
// Reads a plain JSON description of the estate and imports it through spine-import, so this stays a
// data file anyone can edit rather than another hardcoded list.
//
//   node index-infra.mjs --from infra.json --instance nomercy
import { existsSync, readFileSync } from 'node:fs';
import { spawnSync } from 'node:child_process';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { writeFileSync } from 'node:fs';
import { resolveInstance } from './brain-lib.mjs';

const HERE = dirname(fileURLToPath(import.meta.url));
const arg = (flag, fallback = null) => {
  const i = process.argv.indexOf(flag);
  return i > -1 && process.argv[i + 1] && !process.argv[i + 1].startsWith('--') ? process.argv[i + 1] : fallback;
};
const instance = arg('--instance') || resolveInstance();
const from = arg('--from') || join(HERE, 'seeds', 'infra.json');

if (!existsSync(from)) {
  console.error(`no infra description at ${from}`);
  console.error('expected shape: { "hosts": [{id,label,detail}], "services": [{id,label,detail,runsOn}], "registries": [...] }');
  process.exit(1);
}

const spec = JSON.parse(readFileSync(from, 'utf8'));
const nodes = [];
const links = [];

// Each section maps onto a node kind the vocab already knows, so nothing here needs a schema change.
const SECTIONS = [
  ['hosts', 'platform', 'host'],
  ['services', 'seam', 'service'],
  ['registries', 'reference', 'registry'],
  ['domains', 'reference', 'domain'],
];

for (const [section, kind, scheme] of SECTIONS) {
  for (const item of spec[section] || []) {
    const key = `${scheme}:${item.id}`;
    nodes.push({ k: key, kind, label: item.label || item.id, gloss: item.detail || '', scheme, hard: item.hard ? 1 : 0 });
    if (item.runsOn) links.push({ s: key, p: 'belongs_in', o: `host:${item.runsOn}`, because: item.why || '' });
    for (const c of item.consumes || []) links.push({ s: key, p: 'consumes', o: c, because: '' });
  }
}

const out = join(HERE, 'seeds', 'infra-fragment.json');
writeFileSync(out, JSON.stringify({ nodes, slots: [], links, aliases: [], terms: [], edges: [] }, null, 2));
console.log(`${nodes.length} node(s), ${links.length} link(s) from ${from}`);

const exe = join(HERE, 'bin-cli', 'aitm.exe');
const useExe = existsSync(exe);
const imp = spawnSync(useExe ? exe : 'dotnet', [...(useExe ? [] : [join(HERE, 'bin-cli', 'aitm.dll')]), 'spine-import', '--from', out, '--instance', instance], { encoding: 'utf8' });
console.log((imp.stdout || imp.stderr || '').trim().split('\n').slice(-1)[0]);
