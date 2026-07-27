// Index the whole organisation, not just what happens to be cloned on this disk. A store that only
// knows the local monorepo cannot answer "what repos exist", "where does X live", or "is that thing
// still maintained" — and every one of those questions otherwise costs a web lookup or a guess.
//
// Emits a spine fragment and imports it through the tested spine-import path rather than writing
// nodes directly, so supersession, the FTS rebuild and the mutation log all behave identically.
//
//   node index-org.mjs --orgs NoMercy-Entertainment,NoMercyLabs --instance nomercy [--dry]
import { existsSync, writeFileSync, readdirSync, readFileSync } from 'node:fs';
import { spawnSync } from 'node:child_process';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { resolveInstance } from './brain-lib.mjs';

const HERE = dirname(fileURLToPath(import.meta.url));
const arg = (flag, fallback = null) => {
  const i = process.argv.indexOf(flag);
  return i > -1 && process.argv[i + 1] && !process.argv[i + 1].startsWith('--') ? process.argv[i + 1] : fallback;
};
const instance = arg('--instance') || resolveInstance();
// Scope is deliberate: an operator's personal and unrelated-company repos are not part of the product
// and indexing them only dilutes every answer with things the work will never touch.
const orgs = (arg('--orgs') || '').split(',').map((s) => s.trim()).filter(Boolean);
const searchRoots = (arg('--clones') || 'C:/Projects').split(',').map((s) => s.trim()).filter(Boolean);
const dry = process.argv.includes('--dry');
if (orgs.length === 0) { console.error('usage: node index-org.mjs --orgs <a,b,c> [--instance <name>] [--clones <dir,dir>]'); process.exit(1); }

const FIELDS = 'name,description,primaryLanguage,isPrivate,isArchived,isFork,defaultBranchRef,pushedAt,url,repositoryTopics';

function reposOf(org) {
  const r = spawnSync('gh', ['repo', 'list', org, '--limit', '500', '--json', FIELDS], { encoding: 'utf8', maxBuffer: 32 * 1024 * 1024 });
  if (r.status !== 0) { console.error(`  ! could not list ${org}: ${(r.stderr || '').trim().split('\n')[0]}`); return []; }
  try { return JSON.parse(r.stdout); } catch { return []; }
}

// Where a repo is checked out locally, matched by remote URL rather than by folder name, since a
// clone is frequently named differently from its repo.
function localClones() {
  const byRemote = new Map();
  const visit = (dir, depth) => {
    if (depth > 3) return;
    let entries;
    try { entries = readdirSync(dir, { withFileTypes: true }); } catch { return; }
    if (entries.some((e) => e.name === '.git')) {
      const g = spawnSync('git', ['-C', dir, 'remote', 'get-url', 'origin'], { encoding: 'utf8' });
      if (g.status === 0) {
        const url = (g.stdout || '').trim().replace(/\.git$/, '').replace(/^git@github\.com:/, 'https://github.com/');
        if (url) byRemote.set(url.toLowerCase(), dir.replace(/\\/g, '/'));
      }
    }
    for (const e of entries) {
      if (!e.isDirectory() || e.name === 'node_modules' || e.name === '.git' || e.name.startsWith('.')) continue;
      visit(join(dir, e.name), depth + 1);
    }
  };
  for (const root of searchRoots) visit(root, 0);
  return byRemote;
}

// A cloned JS repo tells us the package it publishes, which is the name consumers actually depend on.
function packageName(dir) {
  try {
    const p = JSON.parse(readFileSync(join(dir, 'package.json'), 'utf8'));
    return typeof p.name === 'string' && !p.private ? p.name : null;
  } catch { return null; }
}

console.log(`indexing ${orgs.length} org(s)`);
const clones = localClones();
console.log(`  found ${clones.size} local clone(s) under ${searchRoots.join(', ')}`);

const nodes = [];
const links = [];
let total = 0, archived = 0, forks = 0, cloned = 0;

for (const org of orgs) {
  const orgKey = `org:${org}`;
  nodes.push({ k: orgKey, kind: 'platform', label: org, gloss: `GitHub organisation ${org}`, scheme: 'org', hard: 0 });

  const repos = reposOf(org);
  console.log(`  ${org}: ${repos.length} repo(s)`);
  for (const r of repos) {
    total++;
    if (r.isArchived) archived++;
    if (r.isFork) forks++;
    const key = `repo:${org}/${r.name}`;
    const lang = r.primaryLanguage?.name || '';
    const topics = (r.repositoryTopics || []).map((t) => t.name || t.topic?.name).filter(Boolean);
    const local = clones.get(String(r.url || '').toLowerCase());
    if (local) cloned++;
    const pkg = local ? packageName(local) : null;

    // The gloss is what recall reads back, so it carries the facts a question would actually ask for.
    const facts = [
      r.description || 'no description',
      lang ? `language ${lang}` : '',
      r.isPrivate ? 'private' : 'public',
      r.isArchived ? 'ARCHIVED' : '',
      r.isFork ? 'fork' : '',
      `default branch ${r.defaultBranchRef?.name || 'unknown'}`,
      r.pushedAt ? `last pushed ${String(r.pushedAt).slice(0, 10)}` : '',
      local ? `cloned at ${local}` : 'not cloned locally',
      pkg ? `publishes npm package ${pkg}` : '',
      topics.length ? `topics: ${topics.join(', ')}` : '',
      r.url || '',
    ].filter(Boolean).join('. ');

    nodes.push({ k: key, kind: 'project', label: `${org}/${r.name}`, gloss: facts, scheme: 'repo', hard: 0 });
    links.push({ s: key, p: 'part_of', o: orgKey, because: '' });
  }
}

const fragment = { nodes, slots: [], links, aliases: [], terms: [], edges: [] };
const out = join(HERE, 'seeds', `org.json`);
writeFileSync(out, JSON.stringify(fragment, null, 2));
console.log(`\n${total} repo(s): ${cloned} cloned, ${archived} archived, ${forks} fork(s)`);
console.log(`wrote ${nodes.length} node(s) + ${links.length} link(s) to ${out}`);

if (dry) { console.log('(dry run — not imported)'); process.exit(0); }

const exe = join(HERE, 'bin-cli', 'aitm.exe');
const useExe = existsSync(exe);
const imp = spawnSync(useExe ? exe : 'dotnet', [...(useExe ? [] : [join(HERE, 'bin-cli', 'aitm.dll')]), 'spine-import', '--from', out, '--instance', instance], { encoding: 'utf8' });
console.log((imp.stdout || imp.stderr || '').trim().split('\n').slice(-1)[0]);
