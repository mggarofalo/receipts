import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import assert from 'node:assert/strict';

const read = p => JSON.parse(fs.readFileSync(p, 'utf8').replace(/^\uFEFF/, ''));
const hash = p => crypto.createHash('sha256').update(fs.readFileSync(p)).digest('hex');

export function assertFrozenInputs({ aPath, goldPath, freeze }) {
  const frozen = { aSha256:hash(aPath), goldSha256:hash(goldPath) };
  assert.equal(frozen.aSha256, freeze.aSha256, 'Original source A changed');
  assert.equal(frozen.goldSha256, freeze.goldSha256, 'Frozen acceptance gold changed');
  return frozen;
}

// Retain provenance from real uploads; a resume never substitutes a new upload.
export function retainedRestoreUploads({ prior, runtime, runId, target, b, sha256 }) {
  assert(['source', 'fresh'].includes(target));
  const bPath = path.resolve(b), expectedHash = sha256.toLowerCase();
  assert.equal(hash(bPath), expectedHash, 'Exact B hash changed');
  const uploads = [];
  for (let n = 1; n <= 2; n++) {
    const attemptPath = path.join(prior, `import-${n}-attempt.json`);
    const responsePath = path.join(prior, `import-${n}-response.json`);
    const exportPath = path.join(prior, `restored-${n}.db`);
    const attempt = read(attemptPath), response = read(responsePath);
    for (const entry of [attempt, response]) {
      assert.equal(entry.phase, 'restore', 'Upload must originate in restore');
      assert.equal(entry.runId, runId, 'Upload runtime identity mismatch');
      assert.equal(path.resolve(entry.runtime), path.resolve(runtime), 'Upload runtime path mismatch');
      assert.equal(entry.target, target, 'Upload target mismatch');
      assert.equal(path.resolve(entry.bPath), bPath, 'Upload B path mismatch');
      assert.equal(entry.bSha256.toLowerCase(), expectedHash, 'Upload B hash mismatch');
      assert(Number.isFinite(Date.parse(entry.utc)), 'Missing upload timestamp');
    }
    assert(Date.parse(attempt.utc) <= Date.parse(response.utc), 'Upload response predates attempt');
    assert.equal(response.status, 200, 'Retained upload was not successful');
    assert(response.body && typeof response.body === 'object', 'Missing original upload response');
    uploads.push({ number:n, attemptPath, attemptSha256:hash(attemptPath), responsePath,
      responseSha256:hash(responsePath), exportPath, exportSha256:hash(exportPath), attempt, response });
  }
  assert(Date.parse(uploads[0].response.utc) <= Date.parse(uploads[1].attempt.utc), 'Upload order mismatch');
  return uploads;
}
