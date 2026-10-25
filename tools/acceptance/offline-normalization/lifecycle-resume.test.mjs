import test from 'node:test';
import assert from 'node:assert/strict';
import crypto from 'node:crypto';
import {assertLifecycleResume,assertSettled,assertRevisionTransition,assertUpgradePreserved,assertAuthBoundaryRecovery,runAuthenticationBoundary} from './lifecycle-resume.mjs';

test('a clean later harness revision preserves the recorded application revision',()=>{
  const application='b'.repeat(40),head='c'.repeat(40),original={RunId:'run',Revision:'a'.repeat(40)};
  const transition={runId:'run',previousRevision:original.Revision,candidateRevision:application};
  assert.deepEqual(assertRevisionTransition(transition,original,head,'',['tools/acceptance/offline-normalization/lifecycle.mjs','docs/acceptance.md']),{applicationRevision:application,harnessRevision:head});
  for(const paths of [[],['src/Application/Handler.cs'],['docs-unrelated/file'],['tools/acceptance/offline-normalization-other/file']])
    assert.throws(()=>assertRevisionTransition(transition,original,head,'',paths));
  assert.throws(()=>assertRevisionTransition(transition,original,head,' M docs/file',['docs/file']));
});

test('the upgrade gate runs before authentication and a fresh baseline follows authentication',async()=>{
  const calls=[];
  const result=await runAuthenticationBoundary({verifyUpgrade:async()=>calls.push('upgrade'),authenticate:async()=>{calls.push('login');return 'token';},captureBaseline:async()=>{calls.push('baseline');return 'pg';}});
  assert.deepEqual(calls,['upgrade','login','baseline']);assert.deepEqual(result,{login:'token',postgres:'pg'});
  calls.length=0;
  await assert.rejects(runAuthenticationBoundary({verifyUpgrade:async()=>{calls.push('upgrade');throw Error('changed');},authenticate:async()=>calls.push('login'),captureBaseline:async()=>calls.push('baseline')}));
  assert.deepEqual(calls,['upgrade']);
});

function authFixture() {
  const digest=value=>crypto.createHash('sha256').update(JSON.stringify(value)).digest('hex');
  const row={Id:'known-audit',EntityType:'ApplicationUser',EntityId:'known-user',Action:'Update',ChangedAt:'2026-10-24T16:53:42Z',ChangedByUserId:null,ChangedByApiKeyId:null,IpAddress:'::1',ChangesJson:JSON.stringify(['ConcurrencyStamp','LastLoginAt','RefreshSessionId','RefreshToken','RefreshTokenExpiresAt'].map(FieldName=>({FieldName,OldValue:'old',NewValue:FieldName==='LastLoginAt'?'2026-10-24T16:53:42Z':'new'})))};
  const before={container:{Id:'pg',Mounts:[]},state:{items:[{Id:'i',Quantity:2}],audits:[{Id:'old',EntityType:'ApplicationUser',EntityId:'known-user'}]}};
  const after=structuredClone(before);after.state.audits.push(row);
  const summary={status:'failed-before-lifecycle-mutations',exitCode:1,failedAssertion:'Upgrade changed complete retained PostgreSQL state',settledSamples:2,samplesEqual:true,postgresContainerPreserved:true,newReceiptCreations:0,newLifecycleMutationRequests:0,completedNewLifecycleCheckpoints:0,lifecycleSuccessRecord:false,runnerLoginAddedAuthAudit:true,auditsBefore:1,auditsAfter:2};
  const record={kind:'assessed-auth-boundary-20261024',runId:'run',failedAttempt:'lifecycle-integrated-636a4e0f',applicationRevision:'b'.repeat(40),userId:row.EntityId,auditId:row.Id,auditRowSha256:digest(row),beforePostgresSha256:'a'.repeat(64),failedRunStartedUtc:'2026-10-24T16:53:00Z',failedRunStoppedUtc:'2026-10-24T16:55:00Z'};
  return {record,before,after,retainedAfter:structuredClone(after),addedAudits:[row],summary,runId:'run',applicationRevision:'b'.repeat(40),beforePostgresSha256:'a'.repeat(64)};
}

test('only the individually bound retained login audit can bridge the failed pre-auth boundary',()=>{
  const f=authFixture();const expected=assertAuthBoundaryRecovery(f);
  assert.deepEqual(expected,f.after);assert.equal(f.before.state.audits.length,1);assert.equal(f.before.state.audits[0].Id,'old');
  for(const mutate of [x=>x.record.auditRowSha256='c'.repeat(64),x=>x.record.beforePostgresSha256='c'.repeat(64),x=>x.record.userId='other',x=>x.record.auditId='other',x=>x.record.runId='other',x=>x.record.applicationRevision='c'.repeat(40),x=>x.record.failedRunStartedUtc='2026-10-24T16:54:00Z',x=>x.addedAudits[0].ChangesJson='[]',x=>x.after.container.Mounts.push({Source:'other'}),x=>x.after.state.items[0].Quantity=3,x=>x.retainedAfter.state.items[0].Quantity=3,x=>x.after.state.audits.push({...x.addedAudits[0],Id:'second'}),x=>x.summary.newLifecycleMutationRequests=1]) {
    const f=authFixture();mutate(f);assert.throws(()=>assertAuthBoundaryRecovery(f));
  }
});

test('revision transition binds the original runtime and clean integrated checkout',()=>{
  const original={RunId:'run',Revision:'a'.repeat(40)};
  const transition={runId:'run',previousRevision:original.Revision,candidateRevision:'b'.repeat(40)};
  assertRevisionTransition(transition,original,'b'.repeat(40),'');
  for(const change of [{runId:'other'},{previousRevision:'c'.repeat(40)},{candidateRevision:'c'.repeat(40)}])
    assert.throws(()=>assertRevisionTransition({...transition,...change},original,'b'.repeat(40),''));
  assert.throws(()=>assertRevisionTransition(transition,original,'b'.repeat(40),' M tracked-file'));
});

test('upgrade preserves complete PostgreSQL state and permits only explicit added migration rows',()=>{
  const before={container:{Id:'pg'},state:{identities:[{Id:'a',Embedding:'[1]',NormalizationRevision:'rev'}],audits:[],replays:[],reviewWork:[]}};
  const old=[{MigrationId:'old',ProductVersion:'10'}],added=[{MigrationId:'new',ProductVersion:'10'}];
  assertUpgradePreserved(before,structuredClone(before),old,[...old,...added],added);
  for(const mutate of [x=>x.state.audits.push({Id:'audit'}),x=>x.state.replays.push({Id:'replay'}),x=>x.state.reviewWork.push({ItemId:'i'}),x=>x.state.identities[0].NormalizationRevision='changed',x=>x.state.identities[0].Embedding='[2]']) {
    const actual=structuredClone(before);mutate(actual);
    assert.throws(()=>assertUpgradePreserved(before,actual,old,[...old,...added],added));
  }
  assert.throws(()=>assertUpgradePreserved(before,before,old,[...old,{MigrationId:'unexpected',ProductVersion:'10'}],added));
  assert.throws(()=>assertUpgradePreserved(before,before,old,[{MigrationId:'old',ProductVersion:'changed'},...added],added));
});

function fixture() {
  const stamp={runId:'a',bSha256:'b',phase:'lifecycle'};
  const line={description:'raw oats',subcategory:'Lifecycle test',quantity:2,unitPrice:3};
  const created=['r1','r2','r3'].map(receiptId=>({receiptId,store:'Lifecycle test store',item:line}));
  created[0].intent={kind:'confirmNew',rememberSource:true};
  const requests=Array.from({length:26},(_,i)=>({url:'/read/'+i,method:'GET'}));
  const responses=requests.map((r,i)=>({...r,status:i===25?409:i===21?204:200,text:'{}'}));
  const set=(n,method,url,body,value)=>{requests[n-2]={method,url,body};responses[n-2]={...responses[n-2],method,url,text:JSON.stringify(value)};};
  const items=created.map((c,i)=>({id:'i'+i,receiptId:c.receiptId,normalizedDescriptionId:'oats',normalizationReviewState:'verified',normalizationReviewReason:i===1?'approved_mapping':'purchase_review',normalizationRevision:'old'+i}));
  for(let i=0;i<3;i++) {
    set([7,14,20][i],'POST',`/api/receipts/${created[i].receiptId}/receipt-items`,line,items[i]);
    set([8,15,21][i],'POST',`/api/receipts/${created[i].receiptId}/transactions`,{},{});
  }
  const financial={...items[1],quantity:3,unitPrice:2,normalizationRevision:'new'};
  set(23,'PUT','/api/receipt-items/i1',{},null);
  set(24,'GET','/api/receipt-items/i1',undefined,financial);
  set(26,'POST','/api/receipt-items/normalization-preview',{}, {targetId:'oats',mappingId:'mapping',mappingRevision:'revision',sourceFingerprint:'source'});
  set(27,'PUT','/api/receipt-items/i1',{normalizationIntent:{kind:'defer',expectedItemRevision:'old1',sourceFingerprint:'source'}},{code:'normalization_item_changed',detail:'Changed'});
  return {baseline:stamp,failure:{...stamp,completed:['provider-unavailable-manual-entry'],syntheticReceiptIds:created.map(c=>c.receiptId)},created,requests,responses};
}
test('assessed stop recovers existing identities without arranging replacements',()=>{
  const result=assertLifecycleResume(fixture(),'a','b');
  assert.deepEqual(result.created,['r1','r2','r3']);
  assert.equal(result.financial.normalizationRevision,'new');
  assert.equal(result.staleRequest.body.normalizationIntent.expectedItemRevision,'old1');
});
test('resume refuses another runtime, later stop, unconfirmed create, different scope and non-stale rejection',()=>{
  for(const mutate of [x=>x.failure.runId='other',x=>x.failure.completed.push('later'),x=>x.responses[5].status=500,x=>x.requests[25].body.normalizationIntent.expectedItemRevision='new',x=>x.responses[25].text='{"detail":"Unrelated","code":"other"}',x=>x.created[1].item={...x.created[1].item,description:'another'},x=>x.requests.push({})]) {
    const value=fixture();mutate(value);assert.throws(()=>assertLifecycleResume(value,'a','b'));
  }
});
test('settlement requires populated active projections and keeps revisions and audit rows intact',()=>{
  const pg={state:{identities:[{Id:'a',Status:'Active',NormalizationRevision:'revision',Embedding:null,EmbeddingModelVersion:null}],audits:[{Id:'audit'}]}};
  assert.throws(()=>assertSettled(pg));
  pg.state.identities[0].Embedding='[1]'; assert.throws(()=>assertSettled(pg));
  pg.state.identities[0].EmbeddingModelVersion='model';
  const before=structuredClone(pg);assertSettled(pg);assert.deepEqual(pg,before);
  const changed=structuredClone(pg);changed.state.identities[0].NormalizationRevision='different';
  assert.throws(()=>assert.deepEqual(changed,before));
  changed.state.identities[0].NormalizationRevision='revision';changed.state.audits.push({Id:'new'});
  assert.throws(()=>assert.deepEqual(changed,before));
});
