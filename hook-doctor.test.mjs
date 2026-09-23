import test from 'node:test';
import assert from 'node:assert/strict';
import { auditHooks } from './hook-doctor.mjs';

const direct = name => ({ command: `node C:/Projects/aitm/${name}.mjs` });
const plugin = name => ({ command: 'node', args: [`\${CLAUDE_PLUGIN_ROOT}/${name}.mjs`] });
const entry = hook => [{ hooks: [hook] }];

test('a dormant plugin overlap does not claim duplicate execution', () => {
  const result = auditHooks(
    { hooks: { Stop: entry(direct('continue-guard')) } },
    {},
    { hooks: { Stop: entry(plugin('continue-guard')) } },
  );
  assert.equal(result.unsafe, false);
  assert.deepEqual(result.overlap, ['Stop:continue-guard.mjs']);
});

test('enabling an overlapping plugin reports duplicate execution', () => {
  const result = auditHooks(
    { hooks: { Stop: entry(direct('continue-guard')) } },
    { enabledPlugins: { 'aitm@nomercylabs': true } },
    { hooks: { Stop: entry(plugin('continue-guard')) } },
  );
  assert.equal(result.unsafe, true);
});

test('direct hooks repeated across scopes are unsafe independently of the plugin', () => {
  const result = auditHooks(
    { hooks: { Stop: entry(direct('continue-guard')) } },
    { hooks: { Stop: entry(direct('continue-guard')) } },
    { hooks: {} },
  );
  assert.deepEqual(result.directDuplicates, ['Stop:continue-guard.mjs']);
  assert.equal(result.unsafe, true);
});
