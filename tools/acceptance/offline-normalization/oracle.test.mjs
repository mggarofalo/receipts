import test from 'node:test';
import assert from 'node:assert/strict';
import { assertInitial, assertPortableGold } from './oracle.mjs';

const gold={receipts:[{location:'Store',items:[{key:'a',description:'Raw A',receiptItemCode:'A',fragmentName:'Fragment A'},{key:'b',description:'Raw B',receiptItemCode:'B',fragmentName:'Fragment B'},{key:'pending'},{key:'fee'}]}],gold:{identities:[{key:'a',name:'Product A',members:['a']},{key:'b',name:'Product B',members:['b']}],verifiedItems:['a','b'],needsReviewItems:['pending'],excludedItems:[{key:'fee'}],rememberedSources:[{itemKey:'a',identityKey:'a'}]}};
const bindings={items:{a:'item-a',b:'item-b',pending:'item-pending',fee:'item-fee'}};
const initial=()=>({'item-a':{normalizationReviewState:'verified',normalizedDescriptionId:'a',normalizedDescriptionName:'Fragment A'},'item-b':{normalizationReviewState:'verified',normalizedDescriptionId:'b',normalizedDescriptionName:'Fragment B'},'item-pending':{normalizationReviewState:'needsReview'},'item-fee':{normalizationReviewState:'needsReview'}});
const portable=()=>({tables:{normalized_descriptions:[{id:'a',canonical_name:'Product A'},{id:'b',canonical_name:'Product B'}],receipt_items:[{id:'item-a',normalized_description_id:'a'},{id:'item-b',normalized_description_id:'b'},{id:'item-pending',normalized_description_id:null},{id:'item-fee',normalized_description_id:null}],normalization_catalog:[...Object.entries(bindings.items).map(([key,id])=>({kind:'review_evidence',payload:JSON.stringify({Id:id,State:key==='pending'?0:key==='fee'?2:1,TargetId:['a','b'].includes(key)?key:null,ExclusionReason:key==='fee'?'Fee':null})})),{kind:'mapping',payload:JSON.stringify({State:'active',Location:'Store',Description:'Raw A',ItemCode:'A',TargetId:'a',Exclude:false})}]}});

test('initial gate rejects premature trust and consolidation',()=>{
  assertInitial(gold,bindings,initial());
  const trusted=initial();trusted['item-pending'].normalizationReviewState='verified';
  assert.throws(()=>assertInitial(gold,bindings,trusted),/Initial review state pending/);
  const merged=initial();merged['item-b'].normalizedDescriptionId='a';
  assert.throws(()=>assertInitial(gold,bindings,merged),/already consolidated/);
});

test('portable pre-import gate rejects wrong identity, review and source scope',()=>{
  assert.deepEqual(assertPortableGold(gold,bindings,portable()),{a:'a',b:'b'});
  const merged=portable();merged.tables.receipt_items[1].normalized_description_id='a';
  assert.throws(()=>assertPortableGold(gold,bindings,merged));
  const reviewed=portable();const review=reviewed.tables.normalization_catalog[2];review.payload=JSON.stringify({...JSON.parse(review.payload),State:1});
  assert.throws(()=>assertPortableGold(gold,bindings,reviewed),/Portable review state pending/);
  const scoped=portable();const mapping=scoped.tables.normalization_catalog.at(-1);mapping.payload=JSON.stringify({...JSON.parse(mapping.payload),Location:'Other store'});
  assert.throws(()=>assertPortableGold(gold,bindings,scoped),/Portable exact scoped rule a/);
});
