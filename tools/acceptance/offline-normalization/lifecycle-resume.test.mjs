import test from 'node:test';
import assert from 'node:assert/strict';
import {assertLifecycleResume,assertSettled,assertRevisionTransition,assertUpgradePreserved} from './lifecycle-resume.mjs';

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
