// Build the release Dockerfile, then run:
// RECEIPTS_TEST_IMAGE=receipts:taxonomy-smoke node --test scripts/taxonomy-image.test.mjs
// No database, production environment, or secrets are used by these tests.
import test from 'node:test';
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';

const image = process.env.RECEIPTS_TEST_IMAGE;
const dll = '/app/tools/TaxonomyMigration/Receipts.TaxonomyMigration.dll';
const runtimeUser = '1654:1654';

function invoke(args) {
  const result = spawnSync('docker', [
    'run', '--rm', '--network', 'none', '--user', runtimeUser,
    '--entrypoint', 'dotnet', image, dll, ...args,
  ], { encoding: 'utf8', timeout: 30_000 });
  assert.ifError(result.error);
  const output = result.stdout + result.stderr;
  // An unrelated Docker/runtime failure must never count as CLI validation.
  assert.doesNotMatch(output, /could not be found|No such file|Unable to find image|permission denied/i);
  assert.doesNotMatch(output, /Running database migrations|Seeding database|Starting API|Now listening on|Secrets loaded/);
  return { ...result, output };
}

test('release image contains an executable taxonomy CLI and rejects invalid arguments', { skip: !image }, () => {
  const result = invoke(['invalid']);
  assert.equal(result.status, 1);
  assert.match(result.output, /Usage: preflight\|apply/);
});

for (const command of ['preflight', 'apply']) {
  test(`isolated ${command} requires explicit migration connection without application startup`, { skip: !image }, () => {
    const result = invoke([command, 'synthetic-smoke', 'synthetic-actor', '/artifacts/frozen-request.json']);
    assert.notEqual(result.status, 0);
    assert.match(result.output, /RECEIPTS_MIGRATION_CONNECTION is required/);
  });
}
