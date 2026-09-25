// A command the CLI does not know must fail. It printed the help text and exited 0, so a script that
// put `--instance nomercy` before the command (2026-09-24, the project-roots update after the layout
// move) read "success" while nothing had run.
import { spawnSync } from 'node:child_process';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const exe = join(dirname(fileURLToPath(import.meta.url)), 'bin-cli', 'aitm.exe');
let passed = 0, failed = 0;
function check(name, actual, expected) {
  if (actual === expected) { passed++; } else { failed++; console.log(`FAIL ${name}: got ${actual}, want ${expected}`); }
}
const run = (...args) => spawnSync(exe, [...args], { encoding: 'utf8' });

const unknown = run('no-such-command', '--instance', 'test');
check('an unknown command exits 2', unknown.status, 2);
check('an unknown command names itself on stderr', /unknown command 'no-such-command'/.test(unknown.stderr), true);

const wrongOrder = run('--instance', 'test', 'selftest');
check('a flag before the command exits 2', wrongOrder.status, 2);
check('a flag before the command says where flags go', /after the command/.test(wrongOrder.stderr), true);

const help = run('help', '--instance', 'test');
check('help still exits 0', help.status, 0);
check('help still prints the usage', /aitm <command>/.test(help.stdout), true);

// Dropped in phase 2 (RESTRUCTURE.md section 2.1): loop-guard.mjs and session-continue.mjs, its only
// readers, are gone, and the NoMercy task record does this job now. start/tick only ever existed as
// loop's own sub-verbs, so retiring loop retires them too. One minor version prints "removed in 0.4"
// before becoming a plain unknown command.
const loopBare = run('loop', '--instance', 'test');
check('loop exits 2', loopBare.status, 2);
check('loop says removed in 0.4', /removed in 0\.4/.test(loopBare.stderr), true);

const loopStart = run('loop', 'start', '3', 'sometask', '--instance', 'test');
check('loop start exits 2', loopStart.status, 2);
check('loop start says removed in 0.4', /removed in 0\.4/.test(loopStart.stderr), true);

const loopTick = run('loop', 'tick', '--instance', 'test');
check('loop tick exits 2', loopTick.status, 2);
check('loop tick says removed in 0.4', /removed in 0\.4/.test(loopTick.stderr), true);

console.log(`${passed} passed, ${failed} failed`);
process.exit(failed ? 1 : 0);
