import test from 'node:test';
import assert from 'node:assert/strict';
import { assertInvalidated, assertRevokedHistory, assertPreservedRows, assertPrerequisites } from './lifecycle.mjs';

const purchase={description:'Oats',quantity:2,unitPrice:3,normalizedDescriptionId:'oats',normalizationReviewState:'verified',normalizationRevision:'old'};
test('source-change oracle rejects retained authority, identity and changed money',()=>{
  const valid={...purchase,normalizationReviewState:'needsReview',normalizedDescriptionId:null,normalizationRevision:'new'};
  assertInvalidated(purchase,valid);
  for(const bad of [{normalizationReviewState:'verified'},{normalizedDescriptionId:'oats'},{normalizationRevision:'old'},{unitPrice:4}]) assert.throws(()=>assertInvalidated(purchase,{...valid,...bad}));
});
test('revocation distinguishes preserved historical links from independent authority',()=>{
  const downgraded={...purchase,normalizationReviewState:'needsReview'};
  assertRevokedHistory(purchase,downgraded);assertRevokedHistory(purchase,purchase,true);
  assert.throws(()=>assertRevokedHistory(purchase,purchase));assert.throws(()=>assertRevokedHistory(purchase,downgraded,true));
  assert.throws(()=>assertRevokedHistory(purchase,{...downgraded,normalizedDescriptionId:null}));
});
test('baseline comparison permits separately added facts but rejects changed, missing and duplicate original rows',()=>{
  const baseline={items:[{id:'a',amount:6},{id:'a',amount:6}]};
  assertPreservedRows(baseline,{items:[...baseline.items,{id:'b',amount:9}]});
  assert.throws(()=>assertPreservedRows(baseline,{items:[{id:'a',amount:6}]}));
  assert.throws(()=>assertPreservedRows(baseline,{items:[{id:'a',amount:7},{id:'a',amount:6}]}));
});
test('prerequisite gate refuses read-only restart claims, a different B and premature followups',()=>{
  const restore=(runId,target)=>({runId,target,bSha256:'a'.repeat(64),utc:'2026-10-24T10:00:00Z',imports:[{status:200},{status:200}],evidence:{},database:{}});
  const restart=runId=>({runId,phase:'restart-api',utc:'2026-10-24T10:01:00Z',restart:{Success:true,Before:{StartTimeStamp:'one'},After:{StartTimeStamp:'two'}},before:{container:'same'},after:{container:'same'},evidence:{},database:{}});
  const records={sourceRestore:restore('a','source'),freshRestore:restore('b','fresh'),sourceRestart:restart('a'),freshRestart:restart('b'),followups:{runId:'a',phase:'followups',utc:'2026-10-24T10:02:00Z',created:Object.fromEntries(Array.from({length:7},(_,i)=>[i,{}])),evidence:{coverage:{totalItems:22,verifiedItems:13,pendingItems:8,excludedItems:1}}}};
  assertPrerequisites(records,'a');
  for(const mutate of [r=>r.sourceRestart.phase='verify-restart',r=>r.freshRestore.bSha256='b'.repeat(64),r=>r.followups.utc='2026-10-24T10:00:00Z',r=>r.freshRestart.restart.After.StartTimeStamp='one']) {const bad=structuredClone(records);mutate(bad);assert.throws(()=>assertPrerequisites(bad,'a'));}
});
