import assert from 'node:assert/strict';

// Independent semantic expectations: never import this module in the proposer.
export function assertIdentities(gold, bindings, items) {
  const identities = {};
  for (const group of gold.gold.identities) {
    const ids = group.members.map(k => items[bindings.items[k]]?.normalizedDescriptionId);
    assert(ids[0], `Missing identity ${group.key}`);
    assert(ids.every(id => id === ids[0]), `Split identity ${group.key}`);
    identities[group.key] = ids[0];
    for (const k of group.members) assert.equal(items[bindings.items[k]].normalizedDescriptionName, group.name);
  }
  assert.equal(new Set(Object.values(identities)).size, gold.gold.identities.length, 'Distinct products merged');
  return identities;
}

export function assertHistory(gold, bindings, rows) {
  assert.equal(rows.length, gold.gold.beefPriceHistory.length, 'Unexpected history membership');
  gold.gold.beefPriceHistory.forEach((expected, n) => {
    const row = rows[n];
    assert.equal(row.id, bindings.items[expected.itemKey], 'History item/order');
    assert.equal(row.receiptId, bindings.receipts[expected.itemKey.split('-')[0]], 'History receipt link');
    assert.equal(row.date.slice(0, 10), expected.date);
    assert.equal(Number(row.unitPrice), Number(expected.unitPrice));
    assert.equal(row.normalizationReviewState, 'verified');
  });
}

export function assertFacts(actual, baseline) {
  assert.deepEqual(actual, baseline, 'Raw factual/financial baseline changed');
}

const restoredOperationDetail = 'Historical evidence restored. Source revisions changed; create a new preview to change restored purchases.';
const catalogRows = (tables, kind) => (tables.normalization_catalog ?? []).filter(row => row.kind === kind);

export function assertOperationArchiveUnchanged(actual, original) {
  for (const kind of ['operation', 'member']) assert.deepEqual(catalogRows(actual, kind), catalogRows(original, kind), `Offline ${kind} provenance changed`);
}

export function assertRestoredOperations(actual, original) {
  const expected = catalogRows(original, 'operation');
  const restored = catalogRows(actual, 'operation');
  assert.equal(restored.length, expected.length, 'Restored operation count changed');
  for (const row of expected) {
    const found = restored.find(value => value.id === row.id);assert(found, 'Missing restored operation');
    const before = JSON.parse(row.payload), after = JSON.parse(found.payload);
    assert.match(after.Revision, /^[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12}$/i);
    assert.notEqual(after.Revision, '00000000-0000-0000-0000-000000000000');
    assert.notEqual(after.Revision, before.Revision, 'Restore did not fence the historical operation revision');
    assert.deepEqual(after, {...before, State:'restored', Revision:after.Revision, ErrorCode:'restored_observation', ErrorDetail:restoredOperationDetail}, 'Restored operation provenance changed');
    assert.deepEqual({...found, payload:row.payload}, row, 'Restored operation envelope changed');
  }
  assert.deepEqual(catalogRows(actual, 'member'), catalogRows(original, 'member'), 'Restored frozen membership changed');
}

export function assertRepeatedRestore(actual, previous) {
  assertRestoredOperations(actual, previous);
  const comparable = tables => {
    const copy = structuredClone(tables);
    for (const row of catalogRows(copy, 'operation')) {
      const payload = JSON.parse(row.payload);
      assert.equal(payload.State, 'restored');assert.equal(payload.ErrorCode, 'restored_observation');assert.equal(payload.ErrorDetail, restoredOperationDetail);
      delete payload.Revision;row.payload = payload;
    }
    return copy;
  };
  assert.deepEqual(comparable(actual), comparable(previous), 'Repeated import is not semantically idempotent');
}

export function assertInitial(gold, bindings, items) {
  const fragments=[];
  for(const receipt of gold.receipts)for(const expected of receipt.items) {
    const item=items[bindings.items[expected.key]];assert(item,`Missing initial ${expected.key}`);
    assert.equal(item.normalizationReviewState,expected.fragmentName?'verified':'needsReview',`Initial review state ${expected.key}`);
    if(expected.fragmentName) {
      assert(item.normalizedDescriptionId,`Missing fragment ${expected.key}`);
      assert.equal(item.normalizedDescriptionName,expected.fragmentName,`Initial fragment name ${expected.key}`);
      fragments.push(item.normalizedDescriptionId);
    }
  }
  assert.equal(new Set(fragments).size,fragments.length,'Initial fragments were already consolidated');
}

// Read the closed artifact independently before any application import.
export function assertPortableGold(gold, bindings, snapshot) {
  const tables=snapshot.tables;
  const catalog=kind=>(tables.normalization_catalog??[]).filter(row=>row.kind===kind).map(row=>JSON.parse(row.payload));
  const reviews=new Map(catalog('review_evidence').map(row=>[row.Id,row]));
  const names=new Map(tables.normalized_descriptions.map(row=>[row.id,row.canonical_name]));
  const items=Object.fromEntries(tables.receipt_items.map(row=>[row.id,{
    normalizedDescriptionId:row.normalized_description_id,
    normalizedDescriptionName:names.get(row.normalized_description_id)
  }]));
  const identities=assertIdentities(gold,bindings,items);
  for(const [keys,state]of [[gold.gold.verifiedItems,1],[gold.gold.needsReviewItems,0],[gold.gold.excludedItems.map(row=>row.key),2]])for(const key of keys) {
    const id=bindings.items[key],review=reviews.get(id);assert(review,`Missing portable review ${key}`);
    assert.equal(review.State,state,`Portable review state ${key}`);
    if(state===2) {assert(review.ExclusionReason,`Missing exclusion reason ${key}`);assert.equal(items[id].normalizedDescriptionId,null);}
    if(state===1)assert.equal(review.TargetId,items[id].normalizedDescriptionId,`Portable reviewed target ${key}`);
  }
  const mappings=catalog('mapping').filter(row=>row.State==='active');
  assert.equal(mappings.length,gold.gold.rememberedSources.length,'Portable reusable rule count');
  for(const expected of gold.gold.rememberedSources) {
    const receipt=gold.receipts.find(row=>row.items.some(item=>item.key===expected.itemKey));
    const item=receipt.items.find(row=>row.key===expected.itemKey);
    const matches=mappings.filter(row=>row.Location===receipt.location.trim()&&row.Description===item.description.trim()&&row.ItemCode===item.receiptItemCode);
    assert.equal(matches.length,1,`Portable exact scoped rule ${expected.itemKey}`);
    assert.equal(matches[0].TargetId,identities[expected.identityKey]);assert.equal(matches[0].Exclude,false);
  }
  return identities;
}
