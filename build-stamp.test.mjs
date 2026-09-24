// A plugin update must rebuild the tool server when its source changed, and must not rebuild when it did not.
import { mkdtempSync, writeFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { needsBuild, writeStamp } from './build-stamp.mjs';

let passed = 0, failed = 0;
function check(name, actual, expected) {
  if (actual === expected) { passed++; } else { failed++; console.log(`FAIL ${name}: got ${actual}, want ${expected}`); }
}

const dir = mkdtempSync(join(tmpdir(), 'aitm-stamp-'));
try {
  const source = join(dir, 'mcp.cs');
  const bin = join(dir, 'bin');
  writeFileSync(source, 'class A {}');
  check('no build yet', needsBuild(bin, source), true);

  rmSync(bin, { recursive: true, force: true });
  (await import('node:fs')).mkdirSync(bin);
  writeFileSync(join(bin, 'mcp.dll'), 'old');
  check('build without a stamp (a 0.2.0 install) is stale', needsBuild(bin, source), true);

  writeStamp(bin, source);
  check('stamped build of the same source is fresh', needsBuild(bin, source), false);

  writeFileSync(source, 'class A { void GraphQuery() {} }');
  check('source changed by an update is stale', needsBuild(bin, source), true);

  rmSync(source);
  check('no source to build from: keep the existing build', needsBuild(bin, source), false);
} finally {
  rmSync(dir, { recursive: true, force: true });
}
console.log(`${passed} passed, ${failed} failed`);
process.exit(failed ? 1 : 0);
