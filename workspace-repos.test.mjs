// A NoMercy repo is found by name through the workspace registry, never by a hard-coded path: the
// 2026-09-24 layout move put nomercy-tv under saas/, and the fixed apps/nomercy-tv/.env path broke the
// idp token mint in every session.
import { mkdtempSync, mkdirSync, writeFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { repoPath } from './workspace-repos.mjs';

let passed = 0, failed = 0;
function check(name, actual, expected) {
  if (actual === expected) { passed++; } else { failed++; console.log(`FAIL ${name}: got ${actual}, want ${expected}`); }
}

const workspace = mkdtempSync(join(tmpdir(), 'grimora-workspace-'));
try {
  mkdirSync(join(workspace, '.claude', 'scripts'), { recursive: true });
  writeFileSync(join(workspace, '.claude', 'scripts', 'repos.sh'),
    'REPOS=(\n\t"saas/nomercy-tv|u|master"\n\t"server/nomercy-media-server|u|dev"\n)\n');
  mkdirSync(join(workspace, 'saas', 'nomercy-tv'), { recursive: true });
  writeFileSync(join(workspace, 'saas', 'nomercy-tv', '.env'),
    'IDP_ADMIN_CLIENT_ID=admin-cli\nIDP_ADMIN_CLIENT_SECRET="s3cret"\n');

  check('a registered repo resolves to its folder', repoPath('nomercy-tv', workspace), join(workspace, 'saas', 'nomercy-tv'));
  check('an unknown repo resolves to null', repoPath('nomercy-icons', workspace), null);

  delete process.env.IDP_ADMIN_CLIENT_ID;
  delete process.env.IDP_ADMIN_CLIENT_SECRET;
  process.env.NOMERCY_WORKSPACE = workspace;
  const { loadAdminCreds } = await import('./idp-creds.mjs');
  const creds = loadAdminCreds();
  check('admin creds come from nomercy-tv/.env wherever the repo lives', creds.clientSecret, 's3cret');
  check('the client id comes from the same file', creds.clientId, 'admin-cli');
} finally {
  rmSync(workspace, { recursive: true, force: true });
}
console.log(`${passed} passed, ${failed} failed`);
process.exit(failed ? 1 : 0);
