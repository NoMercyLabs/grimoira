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
