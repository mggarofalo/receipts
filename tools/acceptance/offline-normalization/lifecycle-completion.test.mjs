import test from 'node:test';
import assert from 'node:assert/strict';
import {EventEmitter} from 'node:events';
import {assertCompletionBoundary,assertIndependentProof,assertRestoreDecision,assertCompletionFacts,completedBeforeRestore,runWithLifecycleFailureCapture,journalBrowserSave,redactedFailure} from './lifecycle-completion.mjs';
import {runAuthenticationBoundary} from './lifecycle-resume.mjs';

test('a post-login settlement, count or export failure captures the complete state including the login audit',async()=>{
  for(const failedPhase of ['settlement','count','export','baseline']) {
    const pg={audits:[],items:[{Id:'existing'}]};const evidence=[];
    const failure=Error('injected '+failedPhase);
    await assert.rejects(runWithLifecycleFailureCapture({run:async()=>{
      await runAuthenticationBoundary({verifyUpgrade:async()=>{},authenticate:async()=>{pg.audits.push({Id:'login-audit'});return {accessToken:'secret-access'};},captureBaseline:async()=>{if(failedPhase==='settlement') throw failure;return structuredClone(pg);}});
      for(const phase of ['count','export','baseline']) if(phase===failedPhase) throw failure;
    },captureFailure:async error=>{evidence.push({error:redactedFailure(error),postgres:structuredClone(pg)});}}),error=>error===failure);
    assert.equal(evidence.length,1);assert.deepEqual(evidence[0].postgres,pg);assert.deepEqual(evidence[0].postgres.audits,[{Id:'login-audit'}]);
  }
});

test('failure diagnostics redact returned tokens and credentials without changing independent PostgreSQL evidence',()=>{
  const error=Error('password-123 access-token refresh-token');
  const safe=redactedFailure(error,['password-123','access-token','refresh-token']);
  for(const secret of ['password-123','access-token','refresh-token']) assert(!JSON.stringify(safe).includes(secret));
  assert(safe.message.includes('[REDACTED]'));
});

function browserFixture() {
  const page=new EventEmitter();let rejectWait,resolveWait;
  page.waitForResponse=()=>new Promise((resolve,reject)=>{resolveWait=resolve;rejectWait=reject;});
  const request={url:()=> 'https://localhost/api/receipt-items/item',method:()=> 'PUT',postDataJSON:()=>({unitPrice:8.25,normalizationIntent:{kind:'defer',expectedItemRevision:'old'}}),failure:()=>({errorText:'connection closed'})};
  const evidence=new Map();
  return {page,request,evidence,rejectWait:error=>rejectWait(error),resolveWait:value=>resolveWait(value),journal:(name,value)=>{assert(!evidence.has(name));evidence.set(name,structuredClone(value));}};
}

test('the exact issued browser mutation remains journaled when its response times out or transport fails',async()=>{
  for(const networkFailure of [false,true]) {
    const f=browserFixture();const timeout=Error('response timeout');
    await assert.rejects(journalBrowserSave({...f,itemId:'item',planned:{unitPrice:8.25},click:async()=>{
      assert(f.evidence.has('browser-save-planned.json'));
      f.page.emit('request',f.request);
      assert.deepEqual(f.evidence.get('browser-save-request.json').body,f.request.postDataJSON());
      if(networkFailure) f.page.emit('requestfailed',f.request);
      f.rejectWait(timeout);
    }}),error=>error===timeout);
    assert.equal(f.evidence.get('browser-save-wait-error.json').issuedRequests,1);
    assert.equal(f.evidence.has('browser-save-request-failed.json'),networkFailure);
    assert.equal(f.page.listenerCount('request'),0);assert.equal(f.page.listenerCount('response'),0);assert.equal(f.page.listenerCount('requestfailed'),0);
  }
});

test('browser response and body failure evidence are retained before the response status oracle runs',async()=>{
  const f=browserFixture();
  const response={request:()=>f.request,url:f.request.url,status:()=>500,text:async()=>{throw Error('body interrupted');}};
  const actual=await journalBrowserSave({...f,itemId:'item',planned:{unitPrice:8.25},click:async()=>{f.page.emit('request',f.request);f.page.emit('response',response);f.resolveWait(response);}});
  assert.equal(actual.status(),500);assert.equal(f.evidence.get('browser-save-response-error.json').message,'body interrupted');
  assert.equal(f.evidence.get('browser-save-response-observed.json').status,500);
  assert(f.evidence.has('browser-save-request.json'));
});

test('missing or inaccessible request bodies remain explicit diagnostics without breaking save observation',async()=>{
  for(const throws of [false,true]) {
    const f=browserFixture();f.request.postDataJSON=()=>{if(throws) throw Error('body inaccessible');return null;};
    const response={request:()=>f.request,url:f.request.url,status:()=>409,text:async()=>'{"code":"normalization_item_changed"}'};
    assert.equal(await journalBrowserSave({...f,itemId:'item',planned:{unitPrice:8.25},click:async()=>{f.page.emit('request',f.request);f.page.emit('response',response);f.resolveWait(response);}}),response);
    const observed=f.evidence.get('browser-save-request.json');assert.equal(observed.body,null);assert.equal(observed.requestBodyAvailable,false);assert.equal(observed.bodyCaptureError,throws?'body inaccessible':null);
    assert.equal(f.page.listenerCount('request'),0);
  }
});

function fixture() {
  const stamp={runId:'run',phase:'lifecycle',bSha256:'sha'};
  const item={id:'i',receiptId:'r2',normalizationReviewState:'verified',normalizationReviewReason:'purchase_review',normalizedDescriptionId:'target',description:'Raw oats',receiptItemCode:'500',quantity:2,unitPrice:3,category:'Food',subcategory:'Lifecycle test'};
  const independent={receiptId:'r2',item};
  const future={receiptId:'r4',item:{id:'future',receiptId:'r4',normalizationReviewState:'needsReview',normalizedDescriptionId:null}};
  const failedItem={...item,normalizationReviewState:'needsReview',normalizationReviewReason:'unavailable',normalizationRevision:'failed-revision'};
  const requests=Array.from({length:48},(_,i)=>({method:'GET',url:'/read/'+i}));
  Object.assign(requests[42],{method:'DELETE',url:'/api/receipts',body:['r2']});
  Object.assign(requests[46],{method:'POST',url:'/api/receipts/r2/restore',body:{}});
  Object.assign(requests[47],{method:'GET',url:'/api/receipt-items/i'});
  const responses=requests.map((r,i)=>({...r,status:[7,21,29].includes(i+2)?409:[10,44,48].includes(i+2)?204:200,text:'{}'}));
  responses[22].text=JSON.stringify(item);responses[47].text=JSON.stringify(failedItem);
  const items=Array.from({length:28},(_,i)=>({Id:'item'+i,DeletedAt:null}));
  items[0]={Id:'i',ReceiptId:'r2',DeletedAt:null,NormalizationReviewState:'NeedsReview',NormalizationReviewReason:'unavailable',NormalizationRevision:'failed-revision',NormalizedDescriptionId:'target',Description:'Raw oats',ReceiptItemCode:'500',Quantity:2,UnitPrice:3};
  return {failure:{...stamp,completed:completedBeforeRestore,syntheticReceiptIds:['r0','r1','r2','r3','r4','r5']},summary:{completed:completedBeforeRestore,status:'failed-safe-stop',failedGate:'soft-delete-restore',runnerExit:1,expectedReviewState:'verified',actualReviewState:'needsReview',counts:{receipts:21,items:28,requests:48},recoveryAttempted:false},arrangement:{...stamp,independent,manual:{receiptId:'r0',item}},revoke:{...stamp,evidence:{future,reviewed:item,revoked:{state:'revoked'}}},merge:stamp,requests,responses,stop:{state:{items,receipts:Array.from({length:21},(_,i)=>({Id:'r'+i,DeletedAt:null}))}}};
}

test('only the retained completed delete/restore demotion boundary resumes with existing IDs',()=>{
  const result=assertCompletionBoundary(fixture(),'run','sha');
  assert.equal(result.independent.item.id,'i');assert.equal(result.future.item.id,'future');assert.equal(result.created.length,6);
  for(const mutate of [
    f=>f.requests.push({method:'POST',url:'/api/receipts'}),
    f=>f.responses[46].status=409,
    f=>f.requests[42].body=['another'],
    f=>f.requests[46].url='/api/receipts/another/restore',
    f=>f.responses[47].text=JSON.stringify({...JSON.parse(f.responses[47].text),normalizationReviewState:'verified'}),
    f=>f.failure.completed=[...completedBeforeRestore,'soft-delete-restore'],
    f=>f.stop.state.items[0].NormalizationRevision='other',
    f=>f.stop.state.items[1].DeletedAt='deleted',
    f=>f.arrangement.runId='other',
    f=>f.summary.recoveryAttempted=true,
    f=>f.revoke.evidence.revoked.state='approved'
  ]) {const f=fixture();mutate(f);assert.throws(()=>assertCompletionBoundary(f,'run','sha'));}
});

function proof() {
  return {Id:'i',DeletedAt:null,NormalizationReviewState:'Verified',NormalizationReviewReason:'purchase_review',NormalizationOrigin:'reviewed',NormalizationMappingId:null,NormalizationAssignmentId:'fresh-decision',NormalizationReviewDecisionId:'fresh-decision',ReviewedTargetId:'target',NormalizedDescriptionId:'target',ReviewedSourceHash:'source',NormalizationSourceHash:'source',ReviewedAt:'now',ReviewedBy:'reviewer',Quantity:2,UnitPrice:3};
}

test('independent proof requires coherent current source, target, provenance and decision',()=>{
  assertIndependentProof({state:{items:[proof()]}},'i');
  for(const change of [{NormalizationReviewState:'NeedsReview'},{NormalizationReviewReason:'approved_mapping'},{NormalizationMappingId:'revoked'},{NormalizationAssignmentId:'old'},{ReviewedSourceHash:'old'},{ReviewedTargetId:'other'},{ReviewedBy:null},{DeletedAt:'deleted'}])
    assert.throws(()=>assertIndependentProof({state:{items:[{...proof(),...change}]}},'i'));
});

test('restore must preserve the exact fresh decision and facts, even when display remains verified',()=>{
  assertRestoreDecision(proof(),{...proof(),NormalizationRevision:'new-item-revision'});
  for(const change of [{NormalizationReviewDecisionId:'replacement'},{ReviewedSourceHash:'other'},{NormalizationReviewState:'NeedsReview'},{UnitPrice:4},{ReviewedBy:'other'}])
    assert.throws(()=>assertRestoreDecision(proof(),{...proof(),...change}));
});

test('all 28 purchases and unknown factual tables remain exact except the explicit browser price change',()=>{
  const before={receipt_items:[{id:'future',quantity:2,unit_price:3,total_amount:6,unit_price_currency:'USD'}],unknown_table:[{id:'keep'}]};
  assertCompletionFacts(before,structuredClone(before));
  const after=structuredClone(before);Object.assign(after.receipt_items[0],{unit_price:4,total_amount:8});
  assertCompletionFacts(before,after,'future');
  for(const mutate of [x=>x.unknown_table=[],x=>x.receipt_items[0].quantity=3,x=>x.receipt_items[0].unit_price_currency='EUR',x=>x.receipt_items[0].total_amount=9]) {
    const bad=structuredClone(after);mutate(bad);assert.throws(()=>assertCompletionFacts(before,bad,'future'));
  }
  assert.throws(()=>assertCompletionFacts(before,after,'unknown'));
});

// The archived resume writer persists embedded records, never transitionFiles.
async function retainedEvidenceFixture(t) {
  const fs=await import('node:fs');const os=await import('node:os');const path=await import('node:path');const crypto=await import('node:crypto');
  const root=fs.mkdtempSync(path.join(os.tmpdir(),'receipts-retained-schema-'));t.after(()=>fs.rmSync(root,{recursive:true,force:true}));
  const runtime=path.join(root,'runtime');fs.mkdirSync(runtime);const prior=path.join(runtime,'original');fs.mkdirSync(prior);
  const hash=file=>crypto.createHash('sha256').update(fs.readFileSync(file)).digest('hex');
  const write=(name,value)=>{const file=path.join(root,name);fs.writeFileSync(file,JSON.stringify(value));return file;};
  const ref=(name,value)=>{const file=write(name,value);return {path:file,sha256:hash(file)};};
  const source={RunId:'run',Revision:'launcher'};write('runtime/source.json',source);
  const before=ref('before.json',{state:[]}),migration=ref('migrations.json',[]),after=ref('after.json',{state:[]});
  const transition={runId:'run',candidateRevision:'old-app',previousRevision:'launcher',beforePostgresPath:before.path,beforePostgresSha256:before.sha256,beforeMigrationHistoryPath:migration.path,beforeMigrationHistorySha256:migration.sha256,afterPostgresPath:after.path,afterPostgresSha256:after.sha256};
  const originalTransition=ref('revision-transition.json',transition);
  const evidence={originalBefore:before,transition:originalTransition,summary:ref('summary.json',{}),addedAudits:ref('audits.json',[]),settled:[ref('settled-1.json',{}),ref('settled-2.json',{})],log:ref('failed.log','failure')};
  const names=['failure.json','baseline.json','before-stale-item.db',...Array.from({length:3},(_,i)=>`created-${i+1}.json`),...['request','response'].flatMap(prefix=>Array.from({length:26},(_,i)=>`${prefix}-${i+2}.json`))];
  const files=Object.fromEntries(names.map(name=>{const file=write('runtime/original/'+name,{});return [name,hash(file)];}));
  const resume={runId:'run',applicationRevision:'old-app',prior,files,launchSource:source,transition,transitionSha256:originalTransition.sha256,authRecovery:{runId:'run',applicationRevision:'old-app',beforePostgresSha256:before.sha256,evidence}};
  const produced=ref('next-original-transition.json',{...transition,candidateRevision:'new-app',previousRevision:'old-app'});
  const next={...transition,candidateRevision:'new-app',previousApplicationRevision:'old-app',originalSourcePath:path.join(runtime,'source.json'),originalSourceSha256:hash(path.join(runtime,'source.json')),originalTransitionPath:produced.path,originalTransitionSha256:produced.sha256,derivation:{preservedEvidence:{[before.path]:before.sha256},compiledEvidence:{[after.path]:after.sha256}}};
  const transitionPath=write('next-transition.json',next);
  return {fs,hash,resume,runtime,transitionPath,next,evidence};
}

test('completion preflight binds the actual retained resume schema without transitionFiles',async t=>{
  const f=await retainedEvidenceFixture(t);const module=await import('./lifecycle-completion.mjs');
  const bindings=module.bindCompletionEvidence(f);
  assert.equal(bindings[f.evidence.transition.path],f.resume.transitionSha256);
  assert.equal(bindings[f.evidence.log.path],f.evidence.log.sha256);
  assert.equal(bindings[f.transitionPath],f.hash(f.transitionPath));
  assert.equal(Object.hasOwn(f.resume,'transitionFiles'),false);
});

test('completion preflight rejects missing or tampered original and derived hash proof',async t=>{
  const f=await retainedEvidenceFixture(t);const {bindCompletionEvidence}=await import('./lifecycle-completion.mjs');
  assert(Object.keys(bindCompletionEvidence(f)).length>0);
  for(const change of [r=>delete r.transitionSha256,r=>delete r.transition.beforePostgresSha256,r=>delete r.authRecovery.evidence.log.sha256,r=>delete r.files['baseline.json'],r=>r.launchSource.Revision='changed',r=>r.authRecovery.evidence.settled=[]]) {
    const resume=structuredClone(f.resume);change(resume);assert.throws(()=>bindCompletionEvidence({...f,resume}));
  }
  for(const file of [f.evidence.log.path,f.evidence.transition.path,f.next.beforePostgresPath,f.next.originalSourcePath]) {
    const original=f.fs.readFileSync(file);f.fs.appendFileSync(file,' ');assert.throws(()=>bindCompletionEvidence(f));f.fs.writeFileSync(file,original);
  }
  for(const change of [n=>delete n.originalSourceSha256,n=>delete n.beforePostgresSha256,n=>delete n.originalTransitionSha256,n=>n.derivation.preservedEvidence={},n=>n.derivation.compiledEvidence[Object.keys(n.derivation.compiledEvidence)[0]]='0'.repeat(64)]) {
    const next=structuredClone(f.next);change(next);f.fs.writeFileSync(f.transitionPath,JSON.stringify(next));assert.throws(()=>bindCompletionEvidence(f));
  }
});
