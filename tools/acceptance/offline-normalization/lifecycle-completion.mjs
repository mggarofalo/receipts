import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';

export function redactedFailure(error,sensitive=[]) {
  return Object.fromEntries(['name','message','stack'].map(key=>[key,sensitive.filter(Boolean).reduce((text,value)=>text.split(value).join('[REDACTED]'),String(error[key]??''))]));
}

export async function runWithLifecycleFailureCapture({run,captureFailure}) {
  try {return await run();} catch(error) {await captureFailure(error);throw error;}
}

// Observe real events; no route interception or retry. Intent exists before click,
// and any observable issued body survives a missing response or browser timeout.
export async function journalBrowserSave({page,itemId,planned,journal,click}) {
  journal('browser-save-planned.json',planned);
  const matches=request=>new URL(request.url()).pathname==='/api/receipt-items/'+itemId && request.method()==='PUT';
  const pending=[];let observedError;
  const record=(name,value)=>{try {journal(name,value);} catch(error) {observedError??=error;}};
  let requestCount=0,responseCount=0,failureCount=0;
  const requestListener=request=>{
    if(!matches(request)) return;
    const suffix=++requestCount===1?'':`-${requestCount}`;
    let body=null,bodyCaptureError=null;
    try {body=request.postDataJSON();} catch(error) {bodyCaptureError=error.message;}
    record(`browser-save-request${suffix}.json`,{url:request.url(),method:request.method(),body,requestBodyAvailable:body!==null && body!==undefined,bodyCaptureError});
  };
  const responseListener=response=>{
    if(!matches(response.request())) return;
    const suffix=++responseCount===1?'':`-${responseCount}`;
    record(`browser-save-response-observed${suffix}.json`,{url:response.url(),status:response.status(),bodyPending:true});
    pending.push((async()=>{
      try {record(`browser-save-response${suffix}.json`,{url:response.url(),status:response.status(),text:await response.text()});}
      catch(error) {record(`browser-save-response-error${suffix}.json`,{url:response.url(),status:response.status(),message:error.message});}
    })());
  };
  const failureListener=request=>{
    if(!matches(request)) return;
    const suffix=++failureCount===1?'':`-${failureCount}`;
    record(`browser-save-request-failed${suffix}.json`,{url:request.url(),method:request.method(),failure:request.failure()});
  };
  page.on('request',requestListener);page.on('response',responseListener);page.on('requestfailed',failureListener);
  try {
    // Attach a rejection handler immediately, even if click itself fails first.
    const waiting=page.waitForResponse(response=>matches(response.request())).then(response=>({response}),error=>({error}));
    await click();
    const result=await waiting;if(result.error) throw result.error;
    return result.response;
  } catch(error) {record('browser-save-wait-error.json',{message:error.message,issuedRequests:requestCount,observedResponses:responseCount,failedRequests:failureCount});throw error;}
  finally {
    page.off('request',requestListener);page.off('response',responseListener);page.off('requestfailed',failureListener);
    await Promise.all(pending);if(observedError) throw observedError;
  }
}

export const completedBeforeRestore = ['provider-unavailable-manual-entry','stale-revision-atomic-rejection','receipt-source-edit-invalidates-authority','revoke-history-and-future-reuse','intentional-same-product-merge'];

// Only the observed request-49 stop is supported. No earlier mutations are replayed.
export function assertCompletionBoundary({failure,summary,arrangement,revoke,merge,requests,responses,stop},runId,bSha256) {
  for(const record of [failure,arrangement,revoke,merge]) {
    assert.equal(record.runId,runId);assert.equal(record.phase,'lifecycle');assert.equal(record.bSha256,bSha256);
  }
  assert.deepEqual(failure.completed,completedBeforeRestore);assert.deepEqual(summary.completed,completedBeforeRestore);
  assert.equal(summary.status,'failed-safe-stop');assert.equal(summary.failedGate,'soft-delete-restore');
  assert.equal(summary.runnerExit,1);assert.equal(summary.expectedReviewState,'verified');assert.equal(summary.actualReviewState,'needsReview');
  assert.deepEqual(summary.counts,{receipts:21,items:28,requests:48});assert.equal(summary.recoveryAttempted,false);
  assert.equal(requests.length,48);assert.equal(responses.length,48);
  for(let i=0;i<48;i++) {
    assert.equal(requests[i].url,responses[i].url);assert.equal(requests[i].method,responses[i].method);
    assert.equal(responses[i].status,[7,21,29].includes(i+2)?409:[10,44,48].includes(i+2)?204:200);
  }
  const {independent,manual}=arrangement;const future=revoke.evidence.future;
  const req=n=>requests[n-2],body=n=>JSON.parse(responses[n-2].text);
  assert.equal(independent.item.normalizationReviewState,'verified');assert.equal(independent.item.normalizationReviewReason,'purchase_review');
  assert.deepEqual(body(24),independent.item);assert.deepEqual(revoke.evidence.reviewed,independent.item);
  assert.equal(revoke.evidence.revoked.state,'revoked');assert.equal(future.item.normalizationReviewState,'needsReview');
  assert.equal(future.item.normalizedDescriptionId,null);assert.equal(future.receiptId,future.item.receiptId);
  assert.equal(req(44).method,'DELETE');assert.equal(req(44).url,'/api/receipts');assert.deepEqual(req(44).body,[independent.receiptId]);
  assert.equal(req(48).method,'POST');assert.equal(req(48).url,`/api/receipts/${independent.receiptId}/restore`);assert.deepEqual(req(48).body,{});
  assert.equal(req(49).method,'GET');assert.equal(req(49).url,'/api/receipt-items/'+independent.item.id);
  const failedItem=body(49);assert.equal(failedItem.normalizationReviewState,'needsReview');assert.equal(failedItem.normalizationReviewReason,'unavailable');
  assert.equal(failedItem.id,independent.item.id);assert.equal(failedItem.receiptId,independent.receiptId);
  assert.equal(failedItem.normalizedDescriptionId,independent.item.normalizedDescriptionId);
  const ids=failure.syntheticReceiptIds;assert.equal(ids.length,6);assert.equal(new Set(ids).size,6);
  for(const id of [manual.receiptId,independent.receiptId,future.receiptId]) assert(ids.includes(id));
  assert.equal(stop.state.items.length,28);assert.equal(stop.state.receipts.length,21);
  assert.equal(stop.state.items.filter(i=>!i.DeletedAt).length,28);assert.equal(stop.state.receipts.filter(i=>!i.DeletedAt).length,21);
  const pgItem=stop.state.items.find(i=>i.Id===failedItem.id);assert(pgItem);assert.equal(pgItem.NormalizationReviewState,'NeedsReview');
  assert.equal(pgItem.NormalizationRevision,failedItem.normalizationRevision);assert.equal(pgItem.NormalizationReviewReason,'unavailable');
  assert.equal(pgItem.NormalizedDescriptionId,failedItem.normalizedDescriptionId);
  for(const [key,apiKey] of [['Description','description'],['ReceiptItemCode','receiptItemCode'],['Quantity','quantity'],['UnitPrice','unitPrice']]) assert.equal(pgItem[key],failedItem[apiKey]);
  const line={description:manual.item.description,receiptItemCode:manual.item.receiptItemCode,quantity:2,unitPrice:3,category:manual.item.category,subcategory:manual.item.subcategory};
  return {independent,future,failedItem,created:ids,line,tag:line.subcategory,location:line.subcategory+' store'};
}

export function assertIndependentProof(pg,id) {
  const item=pg.state.items.find(i=>i.Id===id);assert(item && !item.DeletedAt);
  assert.equal(item.NormalizationReviewState,'Verified');assert.equal(item.NormalizationReviewReason,'purchase_review');
  assert.equal(item.NormalizationOrigin,'reviewed');assert.equal(item.NormalizationMappingId,null);
  assert(item.NormalizationAssignmentId);assert.equal(item.NormalizationAssignmentId,item.NormalizationReviewDecisionId);
  assert.equal(item.ReviewedTargetId,item.NormalizedDescriptionId);assert.equal(item.ReviewedSourceHash,item.NormalizationSourceHash);
  assert(item.ReviewedAt && item.ReviewedBy);return item;
}

export function assertRestoreDecision(before,after) {
  for(const field of ['Id','ReceiptId','Description','ReceiptItemCode','Quantity','UnitPrice','Category','Subcategory','NormalizedDescriptionId','NormalizationAssignmentId','NormalizationReviewDecisionId','NormalizationOrigin','NormalizationMappingId','NormalizationReviewState','NormalizationReviewReason','NormalizationSourceHash','ReviewedSourceHash','ReviewedTargetId','ReviewedAt','ReviewedBy','ReviewMappingRevision'])
    assert.deepEqual(after[field],before[field],`Restore changed ${field}`);
}

export function assertCompletionFacts(before,after,browserItemId,priceChange={initial:3,competing:4}) {
  const expected=structuredClone(before);
  if(browserItemId) {
    const item=expected.receipt_items.filter(i=>i.id===browserItemId);assert.equal(item.length,1);
    assert.equal(Number(item[0].quantity),2);assert.equal(Number(item[0].unit_price),priceChange.initial);assert.equal(Number(item[0].total_amount),2*priceChange.initial);
    const actual=after.receipt_items.filter(i=>i.id===browserItemId);assert.equal(actual.length,1);
    assert.equal(Number(actual[0].unit_price),priceChange.competing);assert.equal(Number(actual[0].total_amount),2*priceChange.competing);
    item[0].unit_price=actual[0].unit_price;item[0].total_amount=actual[0].total_amount;
  }
  const ordered=facts=>Object.fromEntries(Object.entries(facts).map(([table,rows])=>[table,[...rows].sort((a,b)=>JSON.stringify(a).localeCompare(JSON.stringify(b)))]));
  assert.deepEqual(ordered(after),ordered(expected),'Continuation changed factual state outside the explicit browser price edit');
}

// Reconstruct proof from the persisted resume schema. transitionFiles belonged to
// the old runner's in-memory state and was never written to resume.json.
export function bindCompletionEvidence({resume,runtime,transitionPath}) {
  const read=file=>JSON.parse(fs.readFileSync(file,'utf8').replace(/^\uFEFF/,''));
  const hash=file=>crypto.createHash('sha256').update(fs.readFileSync(file)).digest('hex');
  const bindings={};
  const bind=(file,sha)=>{
    assert.equal(typeof file,'string','Evidence path required');assert(path.isAbsolute(file),'Absolute evidence path required');
    assert.equal(typeof sha,'string','Evidence SHA-256 required');assert.match(sha,/^[a-f0-9]{64}$/i,'Evidence SHA-256 required');
    file=path.resolve(file);const relative=path.relative(path.dirname(runtime),file);
    assert(relative && !relative.startsWith('..') && !path.isAbsolute(relative),'Evidence must stay inside private runtime parent');
    assert.equal(hash(file),sha,'Retained evidence hash mismatch');
    if(bindings[file]) assert.equal(bindings[file],sha,'Conflicting evidence hashes');
    bindings[file]=sha;
  };
  const names=['failure.json','baseline.json','before-stale-item.db',...Array.from({length:3},(_,i)=>`created-${i+1}.json`),...['request','response'].flatMap(prefix=>Array.from({length:26},(_,i)=>`${prefix}-${i+2}.json`))];
  assert.deepEqual(Object.keys(resume.files).sort(),names.sort(),'Original lifecycle evidence manifest incomplete');
  for(const [name,sha] of Object.entries(resume.files)) bind(path.join(resume.prior,name),sha);
  const transition=read(transitionPath);bind(transitionPath,hash(transitionPath));
  const bindTransition=record=>{
    for(const key of ['beforePostgres','beforeMigrationHistory','afterPostgres']) bind(record[key+'Path'],record[key+'Sha256']);
  };
  bindTransition(transition);bindTransition(resume.transition);
  assert.equal(transition.runId,resume.runId);
  assert.equal(transition.previousApplicationRevision,resume.applicationRevision,'Completion must start at the retained application revision');
  bind(transition.originalSourcePath,transition.originalSourceSha256);
  assert.equal(path.resolve(transition.originalSourcePath),path.join(runtime,'source.json'));
  assert.deepEqual(read(transition.originalSourcePath),resume.launchSource,'Original launcher provenance changed');
  bind(transition.originalTransitionPath,transition.originalTransitionSha256);
  const producedTransition=read(transition.originalTransitionPath);bindTransition(producedTransition);
  assert.equal(producedTransition.runId,transition.runId);
  assert.equal(producedTransition.previousRevision,resume.applicationRevision);
  assert.equal(producedTransition.candidateRevision,transition.candidateRevision);
  for(const key of ['beforePostgresPath','beforePostgresSha256','afterPostgresPath','afterPostgresSha256','beforeMigrationHistoryPath','beforeMigrationHistorySha256','allowedAddedMigrations'])
    assert.deepEqual(producedTransition[key],transition[key],'Derivation changed retained upgrade evidence');
  assert(transition.derivation,'Derived transition proof required');
  for(const key of ['preservedEvidence','compiledEvidence']) {
    const files=transition.derivation[key];assert(files && Object.keys(files).length>0,'Derived evidence manifest required');
    for(const [file,sha] of Object.entries(files)) bind(file,sha);
  }
  // The assessed recovery embeds the original transition reference and each
  // archived auth-boundary artifact. It is proof of old history, never permission
  // for a new completion login exception.
  const recovery=resume.authRecovery;assert(recovery && recovery.evidence,'Retained auth recovery proof required');
  assert.equal(recovery.runId,resume.runId);assert.equal(recovery.applicationRevision,resume.applicationRevision);
  assert.equal(recovery.beforePostgresSha256,resume.transition.beforePostgresSha256);
  const evidence=recovery.evidence;
  for(const key of ['originalBefore','transition','summary','addedAudits','log']) bind(evidence[key]?.path,evidence[key]?.sha256);
  assert(Array.isArray(evidence.settled) && evidence.settled.length===2,'Both retained settlement samples required');
  assert.notEqual(evidence.settled[0].path,evidence.settled[1].path);
  for(const entry of evidence.settled) bind(entry.path,entry.sha256);
  assert.equal(path.resolve(evidence.originalBefore.path),path.resolve(resume.transition.beforePostgresPath));
  assert.equal(evidence.originalBefore.sha256,resume.transition.beforePostgresSha256);
  assert.equal(evidence.transition.sha256,resume.transitionSha256);
  assert.deepEqual(read(evidence.transition.path),resume.transition,'Embedded original transition differs from retained bytes');
  return bindings;
}
