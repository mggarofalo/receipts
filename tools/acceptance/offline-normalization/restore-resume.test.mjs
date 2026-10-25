import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import crypto from 'node:crypto';
import { retainedRestoreUploads, assertFrozenInputs } from './restore-resume.mjs';

test('resume rejects changed gold and original A against the frozen hashes', () => {
  const directory=fs.mkdtempSync(path.join(os.tmpdir(),'restore-freeze-'));
  try {
    const aPath=path.join(directory,'A.db'),goldPath=path.join(directory,'gold.json');
    fs.writeFileSync(aPath,'original A');fs.writeFileSync(goldPath,'{"expected":"frozen"}');
    const hash=p=>crypto.createHash('sha256').update(fs.readFileSync(p)).digest('hex');
    const freeze={aSha256:hash(aPath),goldSha256:hash(goldPath)};
    assert.deepEqual(assertFrozenInputs({aPath,goldPath,freeze}),freeze);
    fs.writeFileSync(goldPath,'{"expected":"weakened"}');
    assert.throws(()=>assertFrozenInputs({aPath,goldPath,freeze}),/Frozen acceptance gold changed/);
    fs.writeFileSync(goldPath,'{"expected":"frozen"}');fs.writeFileSync(aPath,'different A');
    assert.throws(()=>assertFrozenInputs({aPath,goldPath,freeze}),/Original source A changed/);
  } finally {fs.rmSync(directory,{recursive:true,force:true});}
});

test('resume requires two successful real uploads of the exact artifact on the same runtime', () => {
  const prior = fs.mkdtempSync(path.join(os.tmpdir(), 'restore-ledger-'));
  try {
    const b = path.join(prior, 'B.db'); fs.writeFileSync(b, 'immutable artifact');
    const sha256 = crypto.createHash('sha256').update(fs.readFileSync(b)).digest('hex');
    const options = { prior, runtime:prior, runId:'runtime-a', target:'source', b, sha256 };
    const entry = { phase:'restore', runtime:prior, runId:options.runId, target:'source', bPath:b, bSha256:sha256 };
    for (let n=1;n<=2;n++) {
      fs.writeFileSync(path.join(prior, `import-${n}-attempt.json`), JSON.stringify({...entry, utc:`2026-10-24T15:00:0${n*2}.000Z`}));
      fs.writeFileSync(path.join(prior, `import-${n}-response.json`), JSON.stringify({...entry, utc:`2026-10-24T15:00:0${n*2+1}.000Z`, status:200, body:{receiptsUpdated:8}}));
      fs.writeFileSync(path.join(prior, `restored-${n}.db`), `export ${n}`);
    }
    const retained = retainedRestoreUploads(options);
    assert.equal(retained.length, 2); assert.equal(retained[1].response.status, 200);
    const responsePath = retained[1].responsePath, original = fs.readFileSync(responsePath, 'utf8');
    for (const mutation of [{status:429}, {bSha256:'0'.repeat(64)}, {bPath:path.join(prior,'other.db')}, {runId:'other-runtime'}, {target:'fresh'}, {runtime:path.join(prior,'other')}, {utc:'2026-10-24T14:00:00Z'}]) {
      fs.writeFileSync(responsePath, JSON.stringify({...JSON.parse(original), ...mutation}));
      assert.throws(() => retainedRestoreUploads(options));
    }
    fs.writeFileSync(responsePath, original);
    fs.writeFileSync(b, 'changed artifact');
    assert.throws(() => retainedRestoreUploads(options), /Exact B hash changed/);
    fs.writeFileSync(b, 'immutable artifact');
    fs.unlinkSync(retained[1].exportPath);
    assert.throws(() => retainedRestoreUploads(options), /ENOENT/);
  } finally { fs.rmSync(prior, {recursive:true, force:true}); }
});
