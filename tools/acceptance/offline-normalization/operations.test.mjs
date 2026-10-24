import test from 'node:test';
import assert from 'node:assert/strict';
import { assertOperationArchiveUnchanged, assertRestoredOperations, assertRepeatedRestore } from './oracle.mjs';

const first='11111111-1111-4111-8111-111111111111',second='22222222-2222-4222-8222-222222222222',third='33333333-3333-4333-8333-333333333333';
const original=()=>({receipt_items:[{id:'item',quantity:1}],normalization_catalog:[{kind:'operation',id:'operation',payload:JSON.stringify({Id:'operation',Revision:first,State:'preview',Actor:'reviewer',RequestHash:'hash',ErrorCode:null,ErrorDetail:null,Members:[]})},{kind:'member',id:'member',payload:JSON.stringify({OperationId:'operation',ItemId:'item',State:'pending',SourceRevision:first})}]});
function restored(revision=second) {
  const tables=original(),row=tables.normalization_catalog[0];
  row.payload=JSON.stringify({...JSON.parse(row.payload),Revision:revision,State:'restored',ErrorCode:'restored_observation',ErrorDetail:'Historical evidence restored. Source revisions changed; create a new preview to change restored purchases.'});return tables;
}
function change(tables,key,value){const row=tables.normalization_catalog[0];row.payload=JSON.stringify({...JSON.parse(row.payload),[key]:value});return tables;}

test('restore preserves real operation provenance and frozen membership with only documented transitions',()=>{
  assertOperationArchiveUnchanged(original(),original());assertRestoredOperations(restored(),original());
  assert.throws(()=>assertOperationArchiveUnchanged(restored(),original()),/provenance/);
  for(const [key,value]of [['Actor','other'],['RequestHash','different'],['State','applying'],['ErrorCode',null],['Revision',first]])assert.throws(()=>assertRestoredOperations(change(restored(),key,value),original()));
  const missing=restored();missing.normalization_catalog.pop();assert.throws(()=>assertRestoredOperations(missing,original()),/membership/);
});

test('repeated import ignores only validated regenerated operation revision',()=>{
  assertRepeatedRestore(restored(third),restored(second));
  assert.throws(()=>assertRepeatedRestore(restored(second),restored(second)),/fence/);
  assert.throws(()=>assertRepeatedRestore(change(restored(third),'Actor','other'),restored(second)),/provenance/);
  const changed=restored(third);changed.receipt_items[0].quantity=2;assert.throws(()=>assertRepeatedRestore(changed,restored(second)),/idempotent/);
  const member=restored(third);member.normalization_catalog[1].payload='{}';assert.throws(()=>assertRepeatedRestore(member,restored(second)),/membership/);
});
