import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { corpusPath, validateCorpus, payloadFor, policy, makePlan, evaluate, decisionFor, metricsFor, resolveCredential } from './classification-evaluation.mjs';
const corpus = JSON.parse(await readFile(corpusPath, 'utf8'));
const clone = () => structuredClone(corpus);
const plan = () => makePlan(corpus, { split: 'heldout', limit: 6, maxRequests: 6, maxSpendUsd: 0.02 });
test('dry-run ignores credential opt-in without accessing environment or storage', async () => {
  const env = new Proxy({}, { get() { throw new Error('Environment must not be accessed'); } });
  assert.equal(await resolveCredential({ mode: 'dry-run', apiUserSecrets: true }, { env, read: async () => assert.fail('No storage access'), home: () => assert.fail('No home access') }), undefined);
});
test('explicit live environment key takes precedence without reading secrets', async () => {
  assert.equal(await resolveCredential({ mode: 'live', apiUserSecrets: true }, { env: { TYPESAFE_API_KEY: 'fake-env-key' }, read: async () => assert.fail('No storage access') }), 'fake-env-key');
});
test('live opt-in resolves only API UserSecretsId and canonical named key on both platforms', async () => {
  for (const platform of ['win32', 'linux']) {
    const paths = [];
    const value = await resolveCredential({ mode: 'live', apiUserSecrets: true }, { platform, env: { APPDATA: '/fixture/appdata', HOME: '/fixture/home' }, read: async path => {
      paths.push(String(path));
      return paths.length === 1 ? '<Project><UserSecretsId>fake-api-id</UserSecretsId></Project>' : JSON.stringify({ TYPESAFE_API_KEY: 'fake-secrets-key', OTHER_SECRET: 'not-used' });
    } });
    assert.equal(value, 'fake-secrets-key'); assert.equal(paths.length, 2);
    assert.ok(paths[0].endsWith('/src/Presentation/API/API.csproj'));
    assert.ok(paths[1].replaceAll('\\', '/').endsWith(platform === 'win32' ? '/Microsoft/UserSecrets/fake-api-id/secrets.json' : '/.microsoft/usersecrets/fake-api-id/secrets.json'));
  }
});
test('live opt-in accepts one leading BOM while preserving key content and rejecting malformed JSON', async () => {
  const resolveFake = secrets => {
    let calls = 0;
    return resolveCredential({ mode: 'live', apiUserSecrets: true }, { platform: 'win32', env: { APPDATA: '/fake-only' }, read: async () => ++calls === 1 ? '<UserSecretsId>fake-id</UserSecretsId>' : secrets });
  };
  const json = JSON.stringify({ TYPESAFE_API_KEY: 'fake-\uFEFF-key' });
  assert.equal(await resolveFake(`\uFEFF${json}`), 'fake-\uFEFF-key');
  for (const malformed of [`\uFEFF\uFEFF${json}`, ` \uFEFF${json}`, `\uFEFF${json} trailing`, '\uFEFFSECRET invalid JSON']) {
    await assert.rejects(resolveFake(malformed), error => error.message.includes('TYPESAFE_API_KEY') && !error.message.includes('SECRET') && !error.message.includes('fake-') && !error.message.includes('/fake-only'));
  }
});

test('tracked API project declares the same usable identity resolved by the harness without reading real secrets', async () => {
  const project = await readFile(new URL('../src/Presentation/API/API.csproj', import.meta.url), 'utf8');
  const expectedId = project.match(/<UserSecretsId>([^<]+)<\/UserSecretsId>/)?.[1];
  assert.ok(expectedId);
  let calls = 0;
  const key = await resolveCredential({ mode: 'live', apiUserSecrets: true }, { platform: 'win32', env: { APPDATA: '/fake-only' }, read: async path => {
    if (++calls === 1) return project;
    assert.ok(String(path).replaceAll('\\', '/').endsWith(`/Microsoft/UserSecrets/${expectedId}/secrets.json`));
    return '{"TYPESAFE_API_KEY":"fake-only-key"}';
  } });
  assert.equal(key, 'fake-only-key');
});
test('missing canonical key is rejected without revealing other synthetic secrets', async () => {
  let calls = 0;
  await assert.rejects(resolveCredential({ mode: 'live', apiUserSecrets: true }, { platform: 'linux', env: { HOME: '/fake-only' }, read: async () => ++calls === 1 ? '<UserSecretsId>fake-id</UserSecretsId>' : '{"OTHER":"never-log-me"}' }), error => error.message.includes('TYPESAFE_API_KEY') && !error.message.includes('never-log-me'));
});
test('missing, malformed and unsafe secret sources fail with redacted actionable errors', async () => {
  for (const project of ['<Project/>', '<UserSecretsId>../bad</UserSecretsId>', '<UserSecretsId>fake-id</UserSecretsId>']) {
    let calls = 0;
    await assert.rejects(resolveCredential({ mode: 'live', apiUserSecrets: true }, { env: { APPDATA: '/fixture' }, platform: 'win32', read: async () => ++calls === 1 ? project : 'SECRET invalid JSON' }), error => error.message.includes('TYPESAFE_API_KEY') && !error.message.includes('SECRET') && !error.message.includes('/fixture'));
  }
  await assert.rejects(resolveCredential({ mode: 'live' }, { env: {}, read: async () => assert.fail('No implicit storage access') }), /api-user-secrets/);
});
function response(category = 'milk', fit = 0.9) {
  return { model: policy.model, answers: { category: { type: 'choice', choice: category, confidence: 0.8, probabilities: Object.fromEntries(corpus.categories.map(c => [c.id, c.id === category ? 1 : 0])) }, fits: { type: 'noul', noul: fit } }, usage: { input_tokens: 200, output_tokens: 20 } };
}
test('synthetic corpus retains held-out boundaries and broad/no-fit labels', () => {
  assert.equal(validateCorpus(corpus), corpus);
  assert.equal(corpus.cases.filter(c => c.split === 'heldout').length, 16);
  assert.ok(corpus.cases.some(c => c.expected.category === 'dairy'));
  assert.ok(corpus.cases.some(c => c.expected.kind === 'abstain'));
});
test('cross-split source groups and normalized repeated text are rejected', () => {
  const groupLeak = clone(); groupLeak.cases[8].sourceGroup = groupLeak.cases[0].sourceGroup;
  assert.throws(() => validateCorpus(groupLeak), /source|leakage/);
  const textLeak = clone(); textLeak.cases[8].description = '  whole   cow milk 1l ';
  assert.throws(() => validateCorpus(textLeak), /leakage/);
});
test('cycles, unknown labels and malformed cases fail before inference', () => {
  const cyclic = clone(); cyclic.categories[0].parent = 'milk'; assert.throws(() => validateCorpus(cyclic), /hierarchy/);
  const invalid = clone(); invalid.cases[0].expected.category = 'missing'; assert.throws(() => validateCorpus(invalid), /label/);
});
test('exact payload includes finite candidates, separate fit judgment and no labels or split identifiers', () => {
  const payload = payloadFor(corpus, corpus.cases[8]);
  assert.deepEqual(Object.keys(payload.state), ['description', 'taxonomyRevision', 'categories']);
  assert.deepEqual(payload.state.categories.map(c => c.definition), corpus.categories.map(c => c.definition));
  assert.ok(payload.questions.fits.instructions.includes('state.categories'));
  assert.equal(payload.model, 'jev-1.14.0');
  assert.equal(payload.questions.category.type, 'choice'); assert.equal(payload.questions.fits.type, 'noul');
  for (const forbidden of ['expected', 'rationale', 'sourceGroup', 'heldout', 'c09']) assert.ok(!JSON.stringify(payload).includes(forbidden));
});
test('explicit request and reserved spend limits are validated before any sends', async () => {
  const full = makePlan(corpus, { split: 'heldout', limit: 100, maxRequests: 15, maxSpendUsd: 0.05 });
  assert.equal(full.availableRows, 16); assert.equal(full.cases.length, 15);
  assert.equal(new Set(full.cases.map(c => c.sourceGroup)).size, 15);
  assert.throws(() => makePlan(corpus, { split: 'heldout', limit: 6, maxRequests: 5, maxSpendUsd: 1 }), /cap/);
  assert.throws(() => makePlan(corpus, { split: 'heldout', limit: 6, maxRequests: 6, maxSpendUsd: 0.001 }), /cap/);
  assert.throws(() => makePlan(corpus, { split: 'heldout', limit: 6, maxSpendUsd: 1 }), /required/);
  let calls = 0; const invalid = plan(); invalid.maxRequests = 0;
  await assert.rejects(evaluate(corpus, invalid, async () => { calls++; return response(); }), /caps/);
  assert.equal(calls, 0);
});
test('one exact source group cannot hide conflicting labels when deduplicated', () => {
  const invalid = clone(); invalid.cases[17].expected.category = 'food';
  assert.throws(() => validateCorpus(invalid), /Inconsistent/);
});
test('response validation rejects unknown IDs, model drift, missing usage and malformed probabilities', () => {
  for (const mutate of [r => r.model = 'jev-latest', r => r.answers.category.choice = 'unknown', r => delete r.usage, r => r.answers.fits.noul = NaN, r => r.answers.category.probabilities.milk = 0.2, r => r.answers.category.confidence = 2, r => delete r.answers.category.probabilities.food]) {
    const invalid = response(); mutate(invalid); assert.throws(() => decisionFor(corpus, invalid), /Invalid/);
  }
});
test('fit truth table handles boundaries; category ancestors derive from one choice', () => {
  assert.equal(decisionFor(corpus, response('milk', 0.3)).reason, 'no-fit');
  assert.equal(decisionFor(corpus, response('milk', 0.5)).reason, 'uncertain-fit');
  assert.deepEqual(decisionFor(corpus, response('milk', 0.7)).path, ['food', 'dairy', 'milk']);
  assert.equal(decisionFor(corpus, response('dairy')).category, 'dairy');
});
test('transport failure consumes one attempt, stops without retries and never becomes abstention', async () => {
  let calls = 0;
  const report = await evaluate(corpus, plan(), async () => { calls++; throw new Error('SECRET provider body'); });
  assert.equal(calls, 1); assert.equal(report.attempted, 1); assert.equal(report.metrics.failures, 1); assert.equal(report.metrics.abstentions, 0); assert.equal(report.metrics.unattempted, 5); assert.equal(report.usageComplete, false);
  assert.ok(!JSON.stringify(report).includes('SECRET'));
});
test('invalid response stops and checkpoints dispatch before transport; successful usage recorded', async () => {
  const snapshots = []; let calls = 0;
  const report = await evaluate(corpus, plan(), async () => { calls++; assert.equal(snapshots.at(-1).attempted, calls); return calls === 1 ? response() : {}; }, async r => snapshots.push(structuredClone(r)));
  assert.equal(calls, 2); assert.equal(report.inputTokens, 200); assert.equal(report.metrics.classified, 1); assert.equal(report.metrics.failures, 1); assert.equal(report.metrics.unattempted, 4);
});
test('metrics use total and classified denominators; hierarchy mistakes stay separate', () => {
  const expected = ['dairy', 'dairy', 'milk', 'cheese', null, null, 'paper', 'produce'];
  const cases = expected.map(category => ({ expected: { kind: category ? 'category' : 'abstain', category } }));
  const results = [{ kind: 'category', category: 'dairy' }, { kind: 'category', category: 'milk' }, { kind: 'category', category: 'dairy' }, { kind: 'category', category: 'paper' }, { kind: 'abstain' }, { kind: 'category', category: 'food' }, { kind: 'failure' }];
  const m = metricsFor(corpus, cases, results);
  assert.equal(m.total, 8); assert.equal(m.classified, 5); assert.equal(m.coverage, 5 / 8); assert.equal(m.selectiveAccuracy, 1 / 5); assert.equal(m.totalAgreement, 2 / 8);
  for (const key of ['broadCorrect', 'harmfulNarrow', 'overBroad', 'wrongBranch', 'correctAbstention', 'noFitMisclassified', 'failures', 'unattempted']) assert.equal(m[key], 1, key);
  assert.equal(metricsFor(corpus, [], []).selectiveAccuracy, null);
});
