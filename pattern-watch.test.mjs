// Every case is a signature bug that reached the owner as a bogus "worth codifying" nudge before it was
// caught. A wrong signature is worse than no signature: it proposes work that makes nothing better.
import { signature } from './pattern-watch.mjs';

const CASES = [
  // The work is the command, not the navigation that precedes it.
  ['cd C:/Projects/aitm && node brain-lib.test.mjs', null, 'cd swallowed the real command'],
  ['Set-Location C:/Projects/aitm; ./build-cli.ps1', null, 'the PowerShell spelling of cd'],
  ['for q in a b; do ./bin-cli/aitm.exe doc "$q"; done', 'aitm doc', 'a shell keyword is not a task'],

  // A redirect and a quote are syntax, not names.
  ['./build.ps1 -Quick 2>&1', null, 'a redirect read as a subcommand'],
  ['"C:/Program Files/nodejs/node.exe" index-code.mjs', null, 'quotes collapsed it to the interpreter'],

  // A runner names no task; the script after it does.
  ['npm run build', 'npm run build', 'npm run alone is meaningless'],
  ['yarn run test:unit', 'yarn run test:unit', 'same for yarn'],

  // Nothing that is already deterministic should be proposed for codification.
  ['node continue-guard.test.mjs', null, 'a checked-in script IS the codified form'],
  ['python scripts/gradle-gate.py', null, 'same, whatever the language'],
  ['git rm --cached x', null, 'a vcs primitive is a step, not a procedure'],
  ['git status --short', null, 'same'],
  ['grep -rn foo src', null, 'a bare utility can never become a script'],

  // Real tasks still register.
  ['gh run watch 123 --exit-status', 'gh run', 'a CLI subcommand is a task'],
  ['dotnet build aitm.cs -c Release', 'dotnet build', 'so is a build'],
  ['git commit -q -m "x"', 'git commit', 'commit is a task, unlike git add'],
];

let pass = 0, fail = 0;
for (const [cmd, want, why] of CASES) {
  const got = signature(cmd);
  // null means "suppressed"; the script-name cases resolve to whatever survives, so compare loosely
  // where the expectation is a suppression and exactly where it is a signature.
  const ok = want === null ? (got === null || got === undefined) : got === want;
  console.log(`  ${ok ? 'ok  ' : 'FAIL'} ${String(got).padEnd(22)} ${why}`);
  if (!ok && want !== null) console.log(`       expected "${want}"`);
  ok ? pass++ : fail++;
}
console.log(`\n${pass} passed, ${fail} failed`);
process.exit(fail === 0 ? 0 : 1);
