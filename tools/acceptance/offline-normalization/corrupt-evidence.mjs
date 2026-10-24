import assert from 'node:assert/strict';
import fs from 'node:fs';
import { isDeepStrictEqual } from 'node:util';
import { assertRevisionTransition } from './lifecycle-resume.mjs';

export function assertCorruptRevisionBoundary({transition, launcherSource, runId, head, workingTree, changedPaths}) {
  assert.equal(launcherSource.RunId, runId, 'Launcher source does not belong to the ready runtime');
  const original = launcherSource;
  // An unupgraded runtime still needs the exact same clean/harness-only boundary.
  const binding = transition ?? {runId, previousRevision: original.Revision, candidateRevision: original.Revision};
  return assertRevisionTransition(binding, original, head, workingTree, changedPaths);
}

// Retain the actual wire response before parsing it or asserting its contract.
// A failed media/status/JSON assertion must leave independently readable evidence.
export async function retainCorruptResponse(response, destination) {
  const retained = {
    utc: new Date().toISOString(),
    status: response.status,
    headers: Object.fromEntries(response.headers),
    contentType: response.headers.get('content-type'),
    rawBody: await response.text(),
  };
  const fd = fs.openSync(destination, 'wx');
  try {
    fs.writeFileSync(fd, JSON.stringify(retained, null, 2));
    fs.fsyncSync(fd);
  } finally {
    fs.closeSync(fd);
  }
  return retained;
}

export function assertCorruptResponse(response, name) {
  assert.equal(response.status, 400, `${name} was not explicitly rejected`);
  assert.match(response.contentType ?? '', /^application\/problem\+json(?:\s*;|$)/i,
    `${name} requires application/problem+json`);
  const body = JSON.parse(response.rawBody);
  assert(body && typeof body === 'object' && !Array.isArray(body), `${name} requires a problem document`);
  assert.equal(body.status, 400, `${name} requires problem status 400`);
  assert(typeof body.detail === 'string' && body.detail.trim().length > 0,
    `${name} requires explicit problem detail`);
  return body;
}

// Authentication and request telemetry change on ordinary reads. Business rows
// and entity audit rows do not receive exceptions, including soft-deleted rows,
// embeddings, revisions, replay records and review work.
export function completeBusinessEvidence(query) {
  const relations = JSON.parse(query(`SELECT json_agg(schemaname||'.'||tablename ORDER BY schemaname,tablename)
FROM pg_tables WHERE schemaname IN ('library','matching','receipts','ynab')
OR (schemaname='audit' AND tablename='AuditLogs')`));
  assert(Array.isArray(relations), 'Missing business/entity-audit relations');
  assert.equal(relations.length, 31, 'Expected the complete 31-table business/entity-audit corpus');
  assert.equal(new Set(relations).size, relations.length, 'Duplicate PostgreSQL relation');
  assert(relations.includes('audit.AuditLogs'), 'Missing entity audit relation');
  const pieces = relations.flatMap(relation => {
    assert.match(relation, /^(?:library|matching|receipts|ynab)\.[A-Za-z_][A-Za-z0-9_]*$|^audit\.AuditLogs$/);
    const [schema, table] = relation.split('.');
    return ["'" + relation + "'",
      `(SELECT coalesce(jsonb_agg(to_jsonb(x) ORDER BY to_jsonb(x)::text),'[]'::jsonb) FROM "${schema}"."${table}" x)`];
  });
  const state = JSON.parse(query('BEGIN TRANSACTION ISOLATION LEVEL REPEATABLE READ READ ONLY; SELECT json_build_object('
    + pieces.join(',') + '); COMMIT;'));
  assert.deepEqual(Object.keys(state).sort(), [...relations].sort(), 'Incomplete PostgreSQL snapshot');
  return { relations, state };
}

export function assertCompleteRollback(before, after, name) {
  // Avoid dumping potentially sensitive audit bodies in assertion output.
  assert(isDeepStrictEqual(after, before), `${name} rejection changed complete PostgreSQL business/entity-audit state`);
}
