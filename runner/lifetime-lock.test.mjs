import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, writeFileSync, existsSync, unlinkSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { acquireLifetimeLock } from './lifetime-lock.mjs';

function fixture(body) {
  const home = mkdtempSync(join(tmpdir(), 'receipts-lifetime-'));
  const permit = 'a'.repeat(32);
  writeFileSync(join(home, '.permit-' + permit), '');
  try { body(home, permit); } finally { rmSync(home, { recursive: true, force: true }); }
}

test('lifetime guard excludes another provider and records its actual child PID', () => fixture((home, permit) => {
  const release = acquireLifetimeLock(home, permit);
  try {
    assert.throws(() => acquireLifetimeLock(home, permit), /busy/);
    assert.ok(existsSync(join(home, '.node.lock')));
  } finally { release(); }
  assert.equal(existsSync(join(home, '.node.lock')), false);
}));

test('a delayed orphan cannot start after recovery revoked its launch permit', () => fixture((home, permit) => {
  unlinkSync(join(home, '.permit-' + permit));
  assert.throws(() => acquireLifetimeLock(home, permit), /busy/);
  assert.equal(existsSync(join(home, '.node.lock')), false);
}));

test('an incomplete lifetime identity is never stolen by a provider', () => fixture((home, permit) => {
  writeFileSync(join(home, '.node.lock'), '');
  assert.throws(() => acquireLifetimeLock(home, permit), /busy/);
}));

test('missing or unsafe launch permits fail before any runtime is constructed', () => fixture((home) => {
  assert.throws(() => acquireLifetimeLock(home, undefined), /busy/);
  assert.throws(() => acquireLifetimeLock(home, '../unsafe'), /busy/);
  assert.equal(existsSync(join(home, '.node.lock')), false);
}));
