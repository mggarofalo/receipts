import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import {assertIndependentProof,assertRestoreDecision,assertCompletionFacts} from './lifecycle-completion.mjs';

// Request-body access is diagnostic: the browser transport may not expose it.
export function matchesPurchasePreview(response) {
  const request=response.request();
  return request.method()==='POST' && new URL(response.url()).pathname==='/api/receipt-items/normalization-preview' && response.status()===200;
}

export function matchesCandidateLookup(response,source) {
  const url=new URL(response.url());
  return response.request().method()==='GET' && response.status()===200 &&
    url.pathname==='/api/receipt-items/normalization-candidates' &&
    ['location','description','itemCode'].every(key=>(url.searchParams.get(key)??'')===(source[key]??''));
}

export async function settleCandidateLookup(response) {
  assert.equal(await response.finished(),null,'Suggested products request did not finish');
  const candidates=await response.json();assert(Array.isArray(candidates),'Suggested products response is not a list');
  return candidates;
}

export function assertBrowserPreviewSource(source,item,pg) {
  assert.equal(source.description,item.description);assert.equal(source.itemCode??null,item.receiptItemCode??null);
  const receipt=pg.state.receipts.find(r=>r.Id===item.receiptId);assert(receipt);
  assert.equal(source.location,receipt.Location);
}

export function assertBeforeWriteAssessment(assessment) {
  assert.equal(assessment.kind,'assessed-browser-before-write-20261024');
  assert.equal(assessment.status,'failed-safe-stop');assert.equal(assessment.runnerExit,1);
  assert.equal(assessment.failedGate,'browser-cache-refresh-and-unsaved-draft');assert.equal(assessment.captureWasReadOnly,true);
  for(const key of ['browserSavePlanned','competingPriceWritePerformed','retryPerformed']) assert.equal(assessment[key],false);
}

export function browserRequestFailure(request,lifecycle) {
  const url=new URL(request.url());
  let failure=null,timing=null;
  try {failure=request.failure();} catch(error) {failure={errorText:error.message};}
  try {timing=request.timing();} catch(error) {timing={captureError:error.message};}
  return {utc:new Date().toISOString(),path:url.pathname,query:url.search,url:url.href,
    method:request.method(),transportFailed:true,errorText:failure?.errorText??null,
    lifecycle,timing,requestChain:previewRequestChain(request)};
}

export function assertPurchasePreview(actual,expected) {
  assert(expected.sourceFingerprint,'Independent preview fingerprint missing');
  for(const key of ['sourceFingerprint','mappingId','mappingRevision','targetId','targetRevision','targetName','excluded','sourceAmbiguous','offset','limit','total','candidates']) assert.deepEqual(actual?.[key],expected[key],`Browser preview differs: ${key}`);
  assert.equal(actual.offset,0);assert.equal(actual.limit,10);
}

export function assertBrowserPurchase(trip,future,pg,source) {
  assert.equal(trip.receipt.receipt.id,future.receiptId);assert.equal(trip.receipt.receipt.location,source.location);
  const item=trip.receipt.items.find(i=>i.id===future.item.id);assert(item,'Browser loaded a different item');
  assert.deepEqual(item,future.item,'Browser item differs from independently fetched item');
  const row=pg.state.items.find(i=>i.Id===item.id);assert(row);
  assert.equal(row.ReceiptId,future.receiptId);assert.equal(row.NormalizationRevision,item.normalizationRevision);
  assert.equal(item.description,source.description);assert.equal(item.receiptItemCode,source.itemCode);
  assert.equal(row.Description,item.description);assert.equal(row.ReceiptItemCode,item.receiptItemCode);assert.equal(row.UnitPrice,item.unitPrice);
}

export function previewRequestChain(request) {
  const chain=[];
  for(let current=request;current;current=current.redirectedFrom()) {
    const entry={url:current.url(),method:current.method()};
    try {entry.postData=current.postData();} catch(error) {entry.postDataError=error.message;}
    try {entry.postDataJSON=current.postDataJSON();} catch(error) {entry.postDataJSONError=error.message;}
    chain.push(entry);
  }
  return chain;
}

// Observe rejection immediately. Awaiting the result later still throws into the
// enclosing lifecycle safe-stop handler, including predicate and timeout errors.
export function observeResponse(page,predicate) {
  const result=page.waitForResponse(predicate).then(response=>({response}),error=>({error}));
  return async()=>{const value=await result;if(value.error) throw value.error;return value.response;};
}

export function loadBrowserBoundary({prior,runtime,runId,bSha256,snapshot,transition,_depth=0}) {
  prior=path.resolve(prior);runtime=path.resolve(runtime);
  assert(_depth<8,'Too many assessed browser continuations');
  if(fs.existsSync(path.join(prior,'browser-before-write-assessment.json'))) return loadBrowserBeforeWrite({prior,runtime,runId,bSha256,snapshot,transition,_depth});
  if(path.basename(prior)==='lifecycle-browser-final-a320674d') return loadBrowserRetry({prior,runtime,runId,bSha256,snapshot,transition});
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

// The sole additional retry boundary is this archived timeout, before any save.
function loadBrowserRetry(args) {
  const {prior,runtime,runId,bSha256,snapshot}=args;
  assert.equal(path.dirname(prior),runtime);
  const read=name=>JSON.parse(fs.readFileSync(path.join(prior,name),'utf8').replace(/^\uFEFF/,''));
  const digest=file=>crypto.createHash('sha256').update(fs.readFileSync(file)).digest('hex');
  const original=loadBrowserBoundary({...args,prior:path.join(runtime,'lifecycle-completion-18c87514')});
  const files={...original.files};
  const bind=(file,sha=digest(file))=>{file=path.resolve(file);assert.equal(path.dirname(file),prior);assert.equal(digest(file),sha);files[file]=sha;};
  for(const name of fs.readdirSync(prior)) {const file=path.join(prior,name);assert(fs.statSync(file).isFile());bind(file);}
  for(const name of ['lifecycle-browser-completion.json','lifecycle-completion.json']) assert(!fs.existsSync(path.join(prior,name)));
  assert(!fs.readdirSync(prior).some(n=>n.startsWith('browser-save-')));
  const assessment=read('safe-stop-assessment.json'),failure=read('failure.json'),continuation=read('continuation.json'),browser=read('browser-continuation.json');
  assert.equal(assessment.status,'failed-safe-stop');assert.equal(assessment.runnerExit,1);assert.equal(assessment.failedGate,'browser-cache-refresh-and-unsaved-draft');
  assert.match(failure.error.message,/waitForResponse: Timeout 30000ms/);assert.equal(assessment.error,failure.error.message);
  assert.deepEqual(assessment.counts,{receipts:21,items:28,journalRequests:2,journalResponses:2,previewResponseObservations:1});
  for(const key of ['browserSavePlanned','competingPriceWritePerformed','retryPerformed','rawPreviewBodyAvailable']) assert.equal(assessment[key],false);
  assert.equal(assessment.futureUnitPrice,3);assert.equal(assessment.postLoginPostgresUnchanged,true);assert.equal(assessment.portableApplicationTablesUnchanged,true);
  assert.deepEqual(assessment.portableDifferences,['backup_metadata']);assert.equal(assessment.portableTableCountIncludingMetadata,18);
  const observations=read('browser-api-observations.json');assert.deepEqual(observations,assessment.browserObservations);
  assert.deepEqual(observations,{apiErrors:[],previewRequests:[{path:'/api/receipt-items/normalization-preview',method:'POST',status:200,objectBody:false}]});
  for(const [name,sha] of Object.entries(assessment.evidenceSha256)) bind(path.join(prior,name),sha);
  for(const record of [failure,continuation,browser,read('baseline.json')]) {assert.equal(record.runId,runId);assert.equal(record.bSha256,bSha256);assert.equal(record.phase,'lifecycle');}
  assert.deepEqual(continuation.transition,args.transition);assert.equal(continuation.prior,original.continuation.prior);
  assert.equal(browser.prior,original.prior);assert.deepEqual(browser.files,original.files);assert.deepEqual(browser.completedEarlier,original.completed);
  assert.equal(browser.newReceiptCreations,0);assert.deepEqual(failure.completed,original.completed);
  for(const [file,sha] of Object.entries(original.files)) assert.equal(digest(file),sha);
  assert.deepEqual(read('upgrade-preserved.json').postgres,original.stop);
  assert.deepEqual(fs.readdirSync(prior).filter(n=>/^request-\d+\.json$/.test(n)).sort(),['request-2.json','request-3.json']);
  assert.deepEqual(fs.readdirSync(prior).filter(n=>/^response-\d+\.json$/.test(n)).sort(),['response-2.json','response-3.json']);
  for(const n of [2,3]) {const req=read(`request-${n}.json`),res=read(`response-${n}.json`);assert.equal(req.method,'GET');assert.equal(res.method,'GET');assert.equal(res.status,200);assert.equal(req.url,res.url);}
  assert.equal(read('request-2.json').url,'/api/normalization-ai/connection');
  assert.equal(read('request-3.json').url,'/api/receipt-items/'+original.future.item.id);assert.deepEqual(JSON.parse(read('response-3.json').text),original.future.item);
  const stop=read('safe-stop-postgres.json');assert.deepEqual(stop,read('baseline.json').postgres);
  const expected=snapshot(path.join(prior,'before-lifecycle.db'));assert.deepEqual(expected.tables,original.expected.tables);assert.deepEqual(snapshot(path.join(prior,'safe-stop.db')).tables,expected.tables);
  const retryAssessment=read('browser-retry-assessment.json');assert.equal(retryAssessment.kind,'assessed-browser-timeout-20261024');
  for(const [name,sha] of Object.entries(retryAssessment.evidenceSha256)) bind(path.join(prior,name),sha);
  assert.equal(retryAssessment.rawPreviewBodyAvailable,false);assert.equal(retryAssessment.captureWasReadOnly,true);
  const allTables=read('safe-stop-all-tables.assessed.json');assertExactLoginEffects(original.allTables,allTables,read('authentication-request.json'),read('authentication-response.json'));
  const groups={items:'receipts.ReceiptItems',receipts:'receipts.Receipts',transactions:'receipts.Transactions',adjustments:'receipts.Adjustments',identities:'matching.NormalizedDescriptions',mappings:'matching.NormalizationSourceMappings',reviewWork:'matching.NormalizationReviewWork',replays:'receipts.ReceiptWriteRequests',operations:'matching.NormalizationCatalogOperations',members:'matching.NormalizationCatalogMembers',audits:'audit.AuditLogs'};
  assert.deepEqual(allTables.container,stop.container);
  for(const [key,table] of Object.entries(groups)) assert.deepEqual(allTables.tables[table],stop.state[key]);
  return {...original,prior,files,stop,allTables,expected,rawPreviewBodyAvailable:false};
}

export function assertBeforeWriteJournal(requests,responses,item) {
  assert.equal(requests.length,4);assert.equal(responses.length,4);
  const urls=['/api/normalization-ai/connection','/api/receipt-items/'+item.id,'/api/receipt-items/normalization-preview','/api/receipt-items/'+item.id];
  for(let i=0;i<4;i++) {
    const req=requests[i],res=responses[i];
    assert.equal(req.url,urls[i]);assert.equal(req.method,i===2?'POST':'GET');
    assert.equal(res.url,req.url);assert.equal(res.method,req.method);assert.equal(res.status,200);
    if(i===1 || i===3) assert.deepEqual(JSON.parse(res.text),item);
  }
}

// A continuation is identified by a validated safe boundary, never by a harness SHA.
function loadBrowserBeforeWrite(args) {
  const {prior,runtime,runId,bSha256,snapshot}=args;
  assert.equal(path.dirname(prior),runtime);
  const read=name=>JSON.parse(fs.readFileSync(path.join(prior,name),'utf8').replace(/^\uFEFF/,''));
  const digest=file=>crypto.createHash('sha256').update(fs.readFileSync(file)).digest('hex');
  const assessment=read('browser-before-write-assessment.json');
  assertBeforeWriteAssessment(assessment);
  const browser=read('browser-continuation.json');
  assert.notEqual(path.resolve(browser.prior),prior,'Browser continuation cycle');
  const original=loadBrowserBoundary({...args,prior:browser.prior,_depth:args._depth+1});
  const files={...original.files};
  const names=fs.readdirSync(prior);assert(names.every(name=>fs.statSync(path.join(prior,name)).isFile()));
  for(const name of names) {
    const file=path.join(prior,name),sha=digest(file);
    if(name!=='browser-before-write-assessment.json') assert.equal(assessment.evidenceSha256[name],sha,'Assessed archive changed');
    files[file]=sha;
  }
  assert.deepEqual(Object.keys(assessment.evidenceSha256).sort(),names.filter(n=>n!=='browser-before-write-assessment.json').sort());
  assert(!names.some(n=>n.startsWith('browser-save-') || n==='lifecycle-browser-completion.json' || n==='lifecycle-completion.json'));
  const failure=read('failure.json'),continuation=read('continuation.json'),baseline=read('baseline.json');
  for(const record of [failure,continuation,browser,baseline]) {
    assert.equal(record.runId,runId);assert.equal(record.bSha256,bSha256);assert.equal(record.phase,'lifecycle');
  }
  assert.deepEqual(failure.completed,original.completed);assert.deepEqual(browser.completedEarlier,original.completed);
  assert.equal(browser.newReceiptCreations,0);assert.deepEqual(browser.files,original.files);
  assert.deepEqual(continuation.transition,args.transition);assert.equal(continuation.prior,original.continuation.prior);
  assert.deepEqual(read('upgrade-preserved.json').postgres,original.stop);
  assert.deepEqual(names.filter(n=>/^request-\d+\.json$/.test(n)).sort(),[2,3,4,5].map(n=>`request-${n}.json`));
  assert.deepEqual(names.filter(n=>/^response-\d+\.json$/.test(n)).sort(),[2,3,4,5].map(n=>`response-${n}.json`));
  assertBeforeWriteJournal([2,3,4,5].map(n=>read(`request-${n}.json`)),[2,3,4,5].map(n=>read(`response-${n}.json`)),original.future.item);
  const preview=read('browser-preview-baseline.json'),actual=read('browser-preview-response.json');
  assert.deepEqual(preview.item,original.future.item);
  assertBrowserPreviewSource(preview.source,original.future.item,preview.postgres);
  assert.deepEqual(read('request-4.json').body,{source:preview.source,query:null,offset:0,limit:10});
  assert.equal(actual.status,200);assertPurchasePreview(actual.body,JSON.parse(read('response-4.json').text));assertPurchasePreview(actual.body,preview.preview);
  const stop=read('safe-stop-postgres.json');assert.deepEqual(stop,baseline.postgres);assert.deepEqual(preview.postgres,stop);
  const expected=snapshot(path.join(prior,'before-lifecycle.db'));
  assert.deepEqual(expected.tables,original.expected.tables);assert.deepEqual(snapshot(path.join(prior,'safe-stop.db')).tables,expected.tables);
  const allTables=read('safe-stop-all-tables.assessed.json');
  assertExactLoginEffects(original.allTables,allTables,read('authentication-request.json'),read('authentication-response.json'));
  const groups={items:'receipts.ReceiptItems',receipts:'receipts.Receipts',transactions:'receipts.Transactions',adjustments:'receipts.Adjustments',identities:'matching.NormalizedDescriptions',mappings:'matching.NormalizationSourceMappings',reviewWork:'matching.NormalizationReviewWork',replays:'receipts.ReceiptWriteRequests',operations:'matching.NormalizationCatalogOperations',members:'matching.NormalizationCatalogMembers',audits:'audit.AuditLogs'};
  assert.deepEqual(allTables.container,stop.container);
  for(const [key,table] of Object.entries(groups)) assert.deepEqual(allTables.tables[table],stop.state[key]);
  return {...original,prior,files,stop,allTables,expected,rawPreviewBodyAvailable:true};
}

export function assertExactLoginEffects(before,after,request,response) {
  assert.deepEqual(before.container,after.container);assert.deepEqual(Object.keys(before.tables),Object.keys(after.tables));assert.equal(Object.keys(after.tables).length,42);
  assert.equal(request.url,'/api/auth/login');assert.equal(request.method,'POST');assert.equal(request.credentialsRedacted,true);
  assert.equal(response.url,request.url);assert.equal(response.method,'POST');assert.equal(response.status,200);assert.equal(response.bodyRedacted,true);
  const start=Date.parse(request.utc),end=Date.parse(response.utc);assert(start<=end);
  const within=t=>assert(Date.parse(t)>=start && Date.parse(t)<=end,'Login effect outside journal interval');
  const tables=structuredClone(before.tables),users=tables['identity.AspNetUsers'];assert.equal(users.length,1);
  const old=users[0],updated=after.tables['identity.AspNetUsers'];assert.equal(updated.length,1);assert.equal(updated[0].Id,old.Id);
  const delta=(table)=>{const original=tables[table],actual=after.tables[table];assert.equal(actual.length,original.length+1);for(const row of original) assert(actual.some(x=>JSON.stringify(x)===JSON.stringify(row)));const added=actual.filter(x=>!original.some(y=>JSON.stringify(x)===JSON.stringify(y)));assert.equal(added.length,1);return added[0];};
  const audit=delta('audit.AuditLogs');assert.equal(audit.EntityType,'ApplicationUser');assert.equal(audit.EntityId,old.Id);assert.equal(audit.Action,'Update');within(audit.ChangedAt);
  const changes=JSON.parse(audit.ChangesJson);assert.deepEqual(changes.map(x=>x.FieldName).sort(),['ConcurrencyStamp','LastLoginAt','RefreshSessionId','RefreshToken','RefreshTokenExpiresAt']);
  // The audit retains .NET 100ns ticks; persisted PostgreSQL timestamps have
  // microsecond precision. Compare all six stored digits, never millisecond dates.
  const asValue=(value,field)=>value===null?null:field.endsWith('At')?String(value).replace(/(\.\d{6})\d(?=[+-]|Z$)/,'$1').replace(/(\.\d*?[1-9])0+(?=[+-]|Z$)/,'$1').replace(/\.0+(?=[+-]|Z$)/,'').replace(/Z$/,'+00:00'):String(value);
  for(const change of changes) {assert.equal(asValue(change.OldValue,change.FieldName),asValue(old[change.FieldName],change.FieldName));assert.equal(asValue(change.NewValue,change.FieldName),asValue(updated[0][change.FieldName],change.FieldName));old[change.FieldName]=updated[0][change.FieldName];}
  within(old.LastLoginAt);assert.deepEqual(users,updated);
  const auth=delta('audit.AuthAuditLogs');assert.equal(auth.EventType,'Login');assert.equal(auth.Success,true);assert.equal(auth.UserId,old.Id);assert.equal(auth.FailureReason,null);within(auth.Timestamp);
  tables['audit.AuditLogs']=after.tables['audit.AuditLogs'];tables['audit.AuthAuditLogs']=after.tables['audit.AuthAuditLogs'];
  assert.deepEqual(tables,after.tables,'Unexplained change outside the exact retained login');
}
