import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import {assertIndependentProof,assertRestoreDecision,assertCompletionFacts} from './lifecycle-completion.mjs';

// A preview response can include a preflight or an absent/non-JSON request body.
export function matchesPurchasePreview(response,source) {
  const request=response.request();
  if(request.method()!=='POST' || new URL(response.url()).pathname!=='/api/receipt-items/normalization-preview' || !response.ok()) return false;
  let body;try {body=request.postDataJSON();} catch {return false;}
  if(!body || typeof body!=='object' || Array.isArray(body) || !body.source || typeof body.source!=='object' || Array.isArray(body.source)) return false;
  return ['location','description','itemCode'].every(key=>body.source[key]===source[key]) && !body.query && body.offset===0;
}

// Observe rejection immediately. Awaiting the result later still throws into the
// enclosing lifecycle safe-stop handler, including predicate and timeout errors.
export function observeResponse(page,predicate) {
  const result=page.waitForResponse(predicate).then(response=>({response}),error=>({error}));
  return async()=>{const value=await result;if(value.error) throw value.error;return value.response;};
}

export function loadBrowserBoundary({prior,runtime,runId,bSha256,snapshot,transition}) {
  prior=path.resolve(prior);runtime=path.resolve(runtime);
  assert.equal(path.dirname(prior),runtime);assert.equal(path.basename(prior),'lifecycle-completion-18c87514');
  const read=name=>JSON.parse(fs.readFileSync(path.join(prior,name),'utf8').replace(/^\uFEFF/,''));
  const digest=file=>crypto.createHash('sha256').update(fs.readFileSync(file)).digest('hex');
  const files={};
  const bind=(file,sha=digest(file))=>{
    file=path.resolve(file);const rel=path.relative(path.dirname(runtime),file);
    assert(rel && !rel.startsWith('..') && !path.isAbsolute(rel),'Evidence must remain private');
    assert.match(sha,/^[a-f0-9]{64}$/i);assert.equal(digest(file),sha,'Retained browser evidence changed');files[file]=sha;
  };
  for(const name of fs.readdirSync(prior)) {const file=path.join(prior,name);assert(fs.statSync(file).isFile());bind(file);}
  assert(!fs.existsSync(path.join(prior,'lifecycle-completion.json')),'Browser gate already passed');
  assert(!fs.readdirSync(prior).some(n=>n.startsWith('browser-save-')),'Browser save has already been attempted');
  const summary=read('safe-stop-summary.json'),continuation=read('continuation.json');
  assert.equal(summary.status,'failed-safe-stop');assert.equal(summary.runnerExit,1);
  assert.equal(summary.failedGate,'browser-cache-refresh-and-unsaved-draft');
  assert(summary.error.includes("Cannot read properties of null (reading 'source')"));
  const completed=['explicit-synthetic-purchase-reconfirmation','soft-delete-restore'];
  assert.deepEqual(summary.completedNewCheckpoints,completed);assert.deepEqual(summary.counts,{receipts:21,items:28,requests:21,responses:21});
  assert.equal(summary.browserSavePlanned,false);assert.equal(summary.retryPerformed,false);assert.equal(summary.postgresUnchangedSinceAfterRestore,true);
  for(const record of [continuation,read('baseline.json'),...completed.map((name,i)=>read(`${i+1}-${name}.json`))]) {
    assert.equal(record.runId,runId);assert.equal(record.bSha256,bSha256);assert.equal(record.phase,'lifecycle');
  }
  assert.deepEqual(continuation.transition,transition);assert.equal(continuation.newReceiptCreations,0);assert.equal(continuation.originalFailurePassed,false);
  for(const [name,sha] of Object.entries(continuation.files)) bind(path.join(continuation.prior,name),sha);
  for(const manifest of [continuation.transitionFiles,continuation.retainedEvidenceFiles]) for(const [file,sha] of Object.entries(manifest)) bind(file,sha);
  const requestNames=fs.readdirSync(prior).filter(n=>/^request-\d+\.json$/.test(n)).sort();
  assert.deepEqual(requestNames,Array.from({length:21},(_,i)=>`request-${i+2}.json`).sort());
  assert.deepEqual(fs.readdirSync(prior).filter(n=>/^response-\d+\.json$/.test(n)).sort(),requestNames.map(n=>n.replace('request','response')).sort());
  for(let n=2;n<=22;n++) {
    const req=read(`request-${n}.json`),res=read(`response-${n}.json`);
    assert.equal(req.method,res.method);assert.equal(req.url,res.url);assert.equal(res.status,[6,12,16].includes(n)?204:200);
    if(![5,6,12,16].includes(n)) assert.equal(req.method,'GET','Unexpected mutation after completed restore');
  }
  const reconfirm=read('1-explicit-synthetic-purchase-reconfirmation.json'),restore=read('2-soft-delete-restore.json');
  assert.deepEqual(read('upgrade-preserved.json').postgres,JSON.parse(fs.readFileSync(transition.afterPostgresPath,'utf8').replace(/^\uFEFF/,'')),'Completed restore does not chain from retained upgrade');
  assert.deepEqual(read('baseline.json').postgres,read('before-reconfirmation-postgres.json'));
  const id=reconfirm.evidence.reviewed.id,receiptId=reconfirm.evidence.reviewed.receiptId;
  assert.deepEqual(read('request-12.json').body,[receiptId]);assert.equal(read('request-12.json').method,'DELETE');
  assert.equal(read('request-16.json').url,`/api/receipts/${receiptId}/restore`);assert.equal(read('request-16.json').method,'POST');assert.deepEqual(read('request-16.json').body,{});
  assert.equal(read('request-6.json').url,'/api/receipt-items/'+id);assert.equal(read('request-6.json').method,'PUT');assert.equal(read('request-6.json').body.normalizationIntent.kind,'confirmExisting');assert.equal(read('request-6.json').body.normalizationIntent.rememberSource,false);
  assert.notEqual(reconfirm.evidence.before.normalizationReviewDecisionId,reconfirm.evidence.reviewed.normalizationReviewDecisionId);
  assert.deepEqual(reconfirm.postgres,read('after-reconfirmation-postgres.json'));
  const stop=read('safe-stop-postgres.json');assert.deepEqual(stop,read('after-repeat-restore-postgres.json'));assert.deepEqual(restore.postgres,stop);
  assertRestoreDecision(assertIndependentProof(reconfirm.postgres,id),assertIndependentProof(stop,id));
  assert.equal(restore.evidence.restoredItem.normalizationReviewState,'verified');assert.equal(restore.evidence.restoredItem.normalizationReviewDecisionId,reconfirm.evidence.reviewed.normalizationReviewDecisionId);
  assert.equal(Math.round(restore.evidence.reportBefore.grandTotal*100)-Math.round(restore.evidence.reportDeleted.grandTotal*100),600);
  assert.equal(restore.report.grandTotal,restore.evidence.reportBefore.grandTotal);assert.equal(restore.coverage.totalItems,28);
  const expected=snapshot(path.join(prior,'after-repeat-restore.db'));
  for(const name of ['before-lifecycle.db','after-reconfirmation.db','1-explicit-synthetic-purchase-reconfirmation.db','2-soft-delete-restore.db']) assertCompletionFacts(expected.facts,snapshot(path.join(prior,name)).facts);
  assert.equal(expected.tables.receipts.length,21);assert.equal(expected.tables.receipt_items.length,28);
  const allTables=read('safe-stop-all-tables.json');assert.equal(Object.keys(allTables.tables).length,42);assert.equal(allTables.container.Id,stop.container.Id);
  const future=JSON.parse(read('response-4.json').text);
  assert.equal(future.unitPrice,3);assert.equal(future.quantity,2);assert.equal(future.normalizationReviewState,'needsReview');
  assert.equal(stop.state.items.find(i=>i.Id===future.id).UnitPrice,3);
  return {prior,files,continuation,stop,allTables,expected,completed,rawRunnerLogAvailable:false,future:{receiptId:future.receiptId,item:future},independentId:id};
}
