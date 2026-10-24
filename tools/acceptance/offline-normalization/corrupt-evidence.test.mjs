import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { retainCorruptResponse, assertCorruptResponse, completeBusinessEvidence, assertCompleteRollback, assertCorruptRevisionBoundary } from './corrupt-evidence.mjs';

test('corrupt preflight applies the shared clean harness boundary with and without a transition', () => {
  const original='a'.repeat(40),application='b'.repeat(40),head='c'.repeat(40);
  const options={launcherSource:{RunId:'runtime',Revision:original},runId:'runtime',head,workingTree:'',changedPaths:['tools/acceptance/offline-normalization/run.mjs']};
  assert.deepEqual(assertCorruptRevisionBoundary(options),{applicationRevision:original,harnessRevision:head});
  const transition={runId:'runtime',previousRevision:original,candidateRevision:application};
  assert.deepEqual(assertCorruptRevisionBoundary({...options,transition}),{applicationRevision:application,harnessRevision:head});
  assert.throws(()=>assertCorruptRevisionBoundary({...options,launcherSource:{...options.launcherSource,RunId:'other-runtime'}}),/Launcher source does not belong/);
  for(const record of [options,{...options,transition}]) {
    assert.throws(()=>assertCorruptRevisionBoundary({...record,workingTree:' M docs/acceptance.md'}),/Commit the integrated candidate/);
    assert.throws(()=>assertCorruptRevisionBoundary({...record,changedPaths:['src/Presentation/API/Program.cs']}),/Application source differs/);
    assert.throws(()=>assertCorruptRevisionBoundary({...record,changedPaths:[]}),/complete candidate-to-HEAD/);
  }
});

const problem = { status: 400, detail: 'Mapping revision does not match its evidence.' };

test('real problem response retains raw bytes and headers and accepts media parameters', async () => {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'corrupt-response-'));
  try {
    const destination = path.join(directory, 'response.json');
    const rawBody = JSON.stringify(problem, null, 2) + '\n';
    const response = new Response(rawBody, { status: 400, headers: {
      'content-type': 'Application/Problem+Json; charset=utf-8', 'x-request-id': 'synthetic-request',
    } });
    const retained = await retainCorruptResponse(response, destination);
    assert.equal(retained.rawBody, rawBody);
    assert.equal(retained.headers['x-request-id'], 'synthetic-request');
    assert.deepEqual(JSON.parse(fs.readFileSync(destination, 'utf8')), retained);
    assert.deepEqual(assertCorruptResponse(retained, 'mapping-revision'), problem);
    await assert.rejects(retainCorruptResponse(new Response('replacement'), destination), /EEXIST/);
    assert.equal(JSON.parse(fs.readFileSync(destination, 'utf8')).rawBody, rawBody);
  } finally {
    fs.rmSync(directory, { recursive: true, force: true });
  }
});

test('wrong media and malformed JSON fail only after their actual response is durable', async () => {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'corrupt-failed-response-'));
  try {
    for (const [name, media, rawBody, expected] of [
      ['ordinary-json', 'application/json', JSON.stringify(problem), /application\/problem\+json/],
      ['suffix-media', 'application/problem+json-invalid', JSON.stringify(problem), /application\/problem\+json/],
      ['invalid-json', 'application/problem+json', '{incomplete', /JSON/],
      ['unexpected-success', 'application/problem+json', JSON.stringify(problem), /not explicitly rejected/],
    ]) {
      const destination = path.join(directory, `${name}.json`);
      const status = name === 'unexpected-success' ? 200 : 400;
      const retained = await retainCorruptResponse(new Response(rawBody, { status, headers: { 'content-type': media } }), destination);
      assert.throws(() => assertCorruptResponse(retained, name), expected);
      const actual = JSON.parse(fs.readFileSync(destination, 'utf8'));
      assert.equal(actual.status, status);
      assert.equal(actual.contentType, media);
      assert.equal(actual.rawBody, rawBody);
    }
  } finally {
    fs.rmSync(directory, { recursive: true, force: true });
  }
});

test('a reasoned rejection requires a string detail and matching problem status', () => {
  const response = { status: 400, contentType: 'application/problem+json' };
  for (const body of [null, [], { status: 400 }, { status: 400, detail: '  ' },
    { status: 400, detail: { message: 'reason' } }, { status: 409, detail: 'reason' }]) {
    assert.throws(() => assertCorruptResponse({ ...response, rawBody: JSON.stringify(body) }, 'corrupt'));
  }
  assert.throws(() => assertCorruptResponse({ ...response, contentType: null, rawBody: JSON.stringify(problem) }, 'corrupt'), /application\/problem\+json/);
});

test('rollback rejects audit, replay, review-work, soft-deleted and embedding changes without printing their values', () => {
  const before = { containerId: 'owned-container', mounts: [{ Source: 'owned-volume' }], state: {
    'audit.AuditLogs': [{ Id: 'audit', ChangesJson: 'synthetic-private-audit-value' }],
    'receipts.ReceiptWriteRequests': [{ Revision: 'replay-before' }],
    'matching.NormalizationReviewWork': [{ State: 'pending' }],
    'receipts.ReceiptItems': [{ DeletedAt: '2026-10-24T00:00:00Z', UnitPrice: 2 }],
    'matching.NormalizedDescriptions': [{ Embedding: [0.2, 0.4], Revision: 'before' }],
  } };
  assert.doesNotThrow(() => assertCompleteRollback(before, structuredClone(before), 'corrupt'));
  for (const relation of Object.keys(before.state)) {
    const after = structuredClone(before);
    after.state[relation][0].unexpectedWrite = true;
    assert.throws(() => assertCompleteRollback(before, after, 'corrupt'), error => {
      assert.match(error.message, /changed complete PostgreSQL/);
      assert(!error.message.includes('synthetic-private-audit-value'));
      return true;
    });
  }
  for (const field of ['containerId', 'mounts']) {
    const after = structuredClone(before); after[field] = 'different';
    assert.throws(() => assertCompleteRollback(before, after, 'corrupt'), /changed complete PostgreSQL/);
  }
});

test('complete snapshot fails closed on missing tables or omitted table data', () => {
  const relations = ['audit.AuditLogs', ...Array.from({ length: 30 }, (_, i) => `receipts.Table${i}`)];
  const state = Object.fromEntries(relations.map(relation => [relation, []]));
  const query = sql => JSON.stringify(sql.includes('FROM pg_tables') ? relations : state);
  assert.deepEqual(completeBusinessEvidence(query), { relations, state });
  assert.throws(() => completeBusinessEvidence(() => JSON.stringify(relations.slice(1))), /31-table/);
  delete state['receipts.Table0'];
  assert.throws(() => completeBusinessEvidence(query), /Incomplete PostgreSQL snapshot/);
  relations[0] = 'audit.RequestTelemetry';
  assert.throws(() => completeBusinessEvidence(query), /Missing entity audit relation/);
});
