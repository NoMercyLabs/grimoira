// Ask how two things are connected, or what a change to one file can reach.
//
//   node brain-path.mjs "NoMercy Connect hub" "nomercy-app-web"      shortest chain between two nodes
//   node brain-path.mjs --blast <file>                               what else carries this file's symbols
//
// "What breaks if I change this" is the question the store exists to answer, and neither shape of it
// was reachable before: impact showed one hop, and nothing showed the chain or the shared surface.
import { resolveInstance, openRead, pathBetween, blastRadius, communities, communityOf, resolveNode } from './brain-lib.mjs';

const argv = process.argv.slice(2);
const instanceFlag = argv.indexOf('--instance');
const instance = instanceFlag > -1 ? argv[instanceFlag + 1] : resolveInstance();
const args = argv.filter((a, i) => a !== '--instance' && i !== instanceFlag + 1);

const db = await openRead(instance);
if (!db) {
  console.error(`no aitm store for instance '${instance}'`);
  process.exit(1);
}

if (args[0] === '--blast') {
  const file = args[1];
  if (!file) { console.error('usage: brain-path.mjs --blast <file>'); process.exit(1); }
  const rows = blastRadius(db, file, { limit: 12 });
  if (rows.length === 0) {
    console.log(`no shared symbols found for ${file} — nothing else in the graph carries its declarations.`);
  } else {
    console.log(`blast radius for ${file}:`);
    for (const r of rows) {
      console.log(`  ${r.symbol.padEnd(34)} ${r.files} file(s), ${r.projects} project(s): ${r.names}`);
    }
  }
  db.close();
  process.exit(0);
}

// The whole estate, read out of the store. The point is that answering "what does this organisation
// consist of" should never require a network call or opening a single file.
if (args[0] === '--org') {
  const filter = args[1] ? args[1].toLowerCase() : null;
  const orgs = db.prepare("SELECT k, label, gloss FROM node WHERE valid_to IS NULL AND scheme='org' ORDER BY label").all();
  if (orgs.length === 0) { console.log('no organisations indexed — run index-org.mjs'); db.close(); process.exit(0); }

  for (const o of orgs) {
    const repos = db.prepare(
      "SELECT label, gloss FROM node WHERE valid_to IS NULL AND scheme='repo' AND label LIKE ? ORDER BY label"
    ).all(`${o.label}/%`);
    const shown = filter ? repos.filter((r) => `${r.label} ${r.gloss}`.toLowerCase().includes(filter)) : repos;
    if (shown.length === 0) continue;

    console.log(`\n${o.label}  (${repos.length} repos${filter ? `, ${shown.length} matching` : ''})`);
    for (const r of shown) {
      const name = r.label.split('/').pop();
      const g = r.gloss || '';
      const lang = (g.match(/language ([A-Za-z+#]+)/) || [])[1] || '-';
      const vis = /\. private\./.test(g) ? 'private' : 'public';
      const arch = /ARCHIVED/.test(g) ? '  [ARCHIVED]' : '';
      const fork = /\. fork\./.test(g) ? '  [fork]' : '';
      const pkg = (g.match(/publishes npm package (\S+?)\./) || [])[1];
      const clone = /cloned at/.test(g) ? 'cloned' : '';
      const desc = g.split('.')[0];
      console.log(`  ${name.padEnd(34)} ${lang.padEnd(12)} ${vis.padEnd(8)} ${clone.padEnd(7)}${arch}${fork}`);
      if (desc && desc !== 'no description') console.log(`      ${desc}`);
      if (pkg) console.log(`      npm: ${pkg}`);
    }
  }
  db.close();
  process.exit(0);
}

// What is being done by hand often enough to deserve being written down once.
if (args[0] === '--patterns') {
  let rows = [];
  try {
    rows = db.prepare(
      `SELECT sig, count, promoted, COALESCE(kind,'command') AS kind, sample
       FROM patterns ORDER BY (kind='sequence') DESC, count DESC LIMIT 30`
    ).all();
  } catch { /* nothing recorded yet */ }
  db.close();
  if (rows.length === 0) { console.log('nothing recorded yet.'); process.exit(0); }

  for (const group of ['sequence', 'command']) {
    const of = rows.filter((r) => r.kind === group);
    if (of.length === 0) continue;
    console.log(`\n${group === 'sequence' ? 'procedures' : 'single commands'}:`);
    for (const r of of) {
      const flag = r.promoted ? ' [settled]' : (r.count >= (group === 'sequence' ? 3 : 5) ? ' <- worth codifying' : '');
      console.log(`  ${String(r.count).padStart(3)}x  ${r.sig}${flag}`);
    }
  }
  process.exit(0);
}

if (args[0] === '--clusters') {
  const found = communities(db);
  db.close();
  if (found.length === 0) { console.log('no clusters — the triple graph has no connected nodes yet.'); process.exit(0); }
  console.log(`${found.length} cluster(s):\n`);
  for (const c of found.slice(0, 12)) {
    console.log(`  ${c.hub}  (${c.size} nodes)`);
    for (const m of c.members.slice(1, 6)) console.log(`      ${m.kind ? `[${m.kind}] ` : ''}${m.label}`);
    if (c.size > 6) console.log(`      … ${c.size - 6} more`);
    console.log('');
  }
  process.exit(0);
}

if (args[0] === '--cluster') {
  const name = args[1];
  if (!name) { console.error('usage: brain-path.mjs --cluster "<node>"'); process.exit(1); }
  const node = resolveNode(db, name);
  if (!node) { console.log(`no node matches "${name}"`); db.close(); process.exit(0); }
  const c = communityOf(db, node.k);
  db.close();
  if (!c) { console.log(`"${node.label}" is not connected to anything else yet.`); process.exit(0); }
  console.log(`${node.label} sits in the "${c.hub}" cluster (${c.size} nodes):\n`);
  for (const m of c.members) console.log(`  ${m.kind ? `[${m.kind}] ` : ''}${m.label}`);
  process.exit(0);
}

const [from, to] = args;
if (!from || !to) {
  console.error(
    'usage: brain-path.mjs "<from>" "<to>"      shortest chain between two nodes\n' +
    '       brain-path.mjs --blast <file>       what else carries this file\'s symbols\n' +
    '       brain-path.mjs --clusters           every cluster in the graph\n' +
    '       brain-path.mjs --cluster "<node>"   the cluster one node belongs to'
  );
  process.exit(1);
}

const result = pathBetween(db, from, to);
db.close();

if (!result.from) { console.log(`no node matches "${from}"`); process.exit(0); }
if (!result.to) { console.log(`no node matches "${to}"`); process.exit(0); }
if (result.hops === null) {
  console.log(`no recorded connection between "${result.from.label}" and "${result.to.label}".`);
  process.exit(0);
}
if (result.hops.length === 0) { console.log('same node.'); process.exit(0); }

console.log(`${result.from.label}  ->  ${result.to.label}   (${result.hops.length} hop(s))`);
for (const h of result.hops) {
  const arrow = h.dir === 'out' ? '->' : '<-';
  console.log(`  ${arrow} ${h.pred}  ${h.to}${h.because ? `   (${h.because})` : ''}`);
}
