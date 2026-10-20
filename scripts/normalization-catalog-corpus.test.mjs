import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';

const corpus = JSON.parse(readFileSync(new URL('../docs/normalization-evaluation/catalog.v2.json', import.meta.url), 'utf8'));
const cases = corpus.cases;

test('catalog corpus declares synthetic proposed labels and bounded splits', () => {
  assert.equal(corpus.schemaVersion, '2');
  assert.equal(corpus.provenance, 'wholly-synthetic');
  assert.equal(corpus.labelStatus, 'proposed-synthetic');
  assert.equal(cases.length, 24);
  assert.equal(cases.filter(c => c.split === 'example').length, 6);
  assert.equal(cases.filter(c => c.split === 'holdout').length, 18);
  assert.equal(new Set(cases.map(c => c.id)).size, cases.length);
  const families = new Map();
  for (const c of cases) {
    assert.ok(['example', 'holdout'].includes(c.split));
    assert.ok(c.id && c.family && c.description && c.tags.length);
    assert.ok(c.description.length <= 500 && c.candidates.length <= 20);
    if (families.has(c.family)) assert.equal(families.get(c.family), c.split, 'a product family must not leak between development and holdout');
    families.set(c.family, c.split);
  }
});

test('expected decisions reference offered candidates and abstentions never invent a target', () => {
  for (const c of cases) {
    const keys = c.candidates.map(x => x.key);
    assert.equal(new Set(keys).size, keys.length, c.id);
    assert.ok(['same', 'different', 'insufficient'].includes(c.expected.decision));
    assert.ok(c.knownMatchKey === null || typeof c.knownMatchKey === 'string');
    if (c.expected.decision === 'same') {
      assert.ok(keys.includes(c.expected.candidateKey), c.id);
      assert.equal(c.expected.candidateKey, c.knownMatchKey, c.id);
    } else assert.equal(c.expected.candidateKey, null, c.id);
    for (const candidate of c.candidates) {
      assert.ok(candidate.name && candidate.name.length <= 200);
      for (const example of candidate.approvedExamples) {
        assert.equal(typeof example.description, 'string');
        assert.equal(typeof example.sameScope, 'boolean');
      }
    }
  }
});

test('retrieval-miss controls have an absent known positive and never force a false selection', () => {
  const misses = cases.filter(c => c.tags.includes('retrieval-miss'));
  assert.ok(misses.length >= 3);
  for (const c of misses) {
    assert.ok(c.knownMatchKey);
    assert.ok(!c.candidates.some(x => x.key === c.knownMatchKey), c.id);
    assert.equal(c.expected.decision, 'insufficient', c.id);
  }
  assert.ok(misses.some(c => c.candidates.length === 0));
});

test('candidate-order controls change only order, preserving expected decisions', () => {
  const pair = cases.filter(c => c.tags.includes('pair:franks-order'));
  assert.equal(pair.length, 2);
  assert.equal(pair[0].description, pair[1].description);
  assert.equal(pair[0].split, pair[1].split);
  assert.deepEqual(pair[0].expected, pair[1].expected);
  assert.deepEqual(pair[0].candidates, [...pair[1].candidates].reverse());
  assert.notDeepEqual(pair[0].candidates, pair[1].candidates);
});

test('example ablations hold descriptions and candidate identities fixed', () => {
  for (const tag of ['ablation:pasta', 'ablation:coffee', 'ablation:pizza']) {
    const withExamples = cases.find(c => c.tags.includes(tag) && c.tags.includes('with-examples'));
    const withoutExamples = cases.find(c => c.tags.includes(tag) && c.tags.includes('without-examples'));
    assert.ok(withExamples && withoutExamples);
    assert.equal(withExamples.split, withoutExamples.split);
    assert.equal(withExamples.description, withoutExamples.description);
    assert.equal(withExamples.knownMatchKey, withoutExamples.knownMatchKey);
    assert.deepEqual(withExamples.candidates.map(c => ({ ...c, approvedExamples: [] })), withoutExamples.candidates);
    assert.ok(withExamples.candidates.some(c => c.approvedExamples.length));
  }
});

test('corpus retains safety counterexamples rather than only easy positive matches', () => {
  for (const tag of ['size-conflict', 'preparation-conflict', 'dietary-conflict', 'concentration-conflict', 'pack-conflict', 'brand-conflict', 'missing-attributes', 'scope-conflict', 'prompt-injection'])
    assert.ok(cases.some(c => c.tags.includes(tag)), tag);
  const scope = cases.find(c => c.tags.includes('scope-conflict'));
  assert.equal(scope.expected.decision, 'insufficient');
  assert.ok(scope.candidates.every(c => c.approvedExamples.every(e => e.sameScope === false)));
  const injection = cases.find(c => c.tags.includes('prompt-injection'));
  assert.equal(injection.expected.candidateKey, 'pizza');
  assert.ok(injection.candidates.some(c => c.approvedExamples.some(e => e.description.includes('pepperoni'))));
});
