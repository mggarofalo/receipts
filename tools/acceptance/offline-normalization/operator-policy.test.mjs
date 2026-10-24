import test from 'node:test';
import assert from 'node:assert/strict';
import { buildPolicy, sha256 } from './operator-policy.mjs';

const source = Buffer.from('synthetic closed source');
const observation = {
  EntityKind: 'item', EntityId: 'private-item-id',
  Source: { Location: 'Synthetic store', Description: 'RAW DESCRIPTION', ItemCode: null, Secret: 'must not leak' },
  SourceHash: 'A'.repeat(64), LinkedCanonicalId: 'hidden-target-id', ReviewState: 'Verified',
};
const inspection = {
  InspectionVersion: 1, SourceSha256: sha256(source), Observations: [observation],
  CanonicalTargets: [{ Name: 'hidden-target-name' }], CurrencyTotals: ['hidden-total'],
};
const bindings = { items: { selected: observation.EntityId }, followups: ['hidden-followup'] };

test('projects only exact eligible source tuples without leaking semantic answers or private bindings', () => {
  const policy = buildPolicy(source, inspection, bindings, ['selected']);
  assert.deepEqual(Object.keys(policy).sort(), ['Attribution', 'Classification', 'EligibleScopes', 'Policy', 'PolicyVersion', 'SourceSha256']);
  assert.deepEqual(policy.EligibleScopes, [{ Source: {
    Location: 'Synthetic store', Description: 'RAW DESCRIPTION', ItemCode: null,
  }, SourceHash: 'A'.repeat(64) }]);
  const packet = JSON.stringify(policy);
  for (const secret of ['hidden-target-id', 'hidden-target-name', 'hidden-total', 'hidden-followup', 'private-item-id', 'must not leak', 'selected'])
    assert.equal(packet.includes(secret), false, secret);
});

test('rejects a different source even if the inspection is otherwise valid', () => {
  assert.throws(() => buildPolicy(Buffer.from('different'), inspection, bindings, ['selected']), /exact source/);
});

test('rejects absent, duplicate and ambiguous bindings instead of guessing eligibility', () => {
  for (const keys of [[], ['missing'], ['selected', 'selected']])
    assert.throws(() => buildPolicy(source, inspection, bindings, keys));
  assert.throws(() => buildPolicy(source, { ...inspection, Observations: [observation, observation] }, bindings, ['selected']), /exactly one/);
  assert.throws(() => buildPolicy(source, inspection, { items: { one: observation.EntityId, two: observation.EntityId } }, ['one', 'two']), /scopes must be unique/);
});
