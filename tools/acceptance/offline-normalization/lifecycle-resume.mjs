import assert from 'node:assert/strict';

export function assertRevisionTransition(transition,original,head,workingTree) {
  assert.equal(transition.runId,original.RunId);
  for(const key of ['previousRevision','candidateRevision']) assert.match(transition[key],/^[a-f0-9]{40}$/i);
  assert.equal(transition.previousRevision,original.Revision,'Transition does not start at the retained runtime revision');
  assert.equal(transition.candidateRevision,head,'Transition does not name the integrated checkout revision');
  assert.equal(workingTree.trim(),'','Commit the integrated candidate and harness before resuming');
}

export function assertUpgradePreserved(before,after,beforeMigrations,afterMigrations,allowedAddedMigrations) {
  assert.deepEqual(after,before,'Upgrade changed complete retained PostgreSQL state');
  assert(Array.isArray(beforeMigrations) && Array.isArray(afterMigrations) && Array.isArray(allowedAddedMigrations));
  const expected=[...beforeMigrations,...allowedAddedMigrations];
  assert.equal(new Set(expected.map(row=>row.MigrationId)).size,expected.length,'Migration transition contains duplicate IDs');
  const sorted=rows=>[...rows].sort((a,b)=>a.MigrationId.localeCompare(b.MigrationId));
  assert.deepEqual(sorted(afterMigrations),sorted(expected),'Migration history differs from the explicit transition');
}

// Recovery is deliberately limited to the observed stop after the first stale PUT.
// A later partial mutation requires a separately assessed recovery protocol.
export function assertLifecycleResume({failure,baseline,created,requests,responses},runId,bSha256) {
  for(const record of [failure,baseline]) {
    assert.equal(record.runId,runId); assert.equal(record.bSha256,bSha256);
    assert.equal(record.phase,'lifecycle');
  }
  assert.deepEqual(failure.completed,['provider-unavailable-manual-entry']);
  assert.equal(created.length,3); assert.equal(requests.length,26); assert.equal(responses.length,26);
  for(let i=0;i<26;i++) {
    assert.equal(requests[i].url,responses[i].url); assert.equal(requests[i].method,responses[i].method);
    assert.equal(responses[i].status,i===25?409:i===21?204:200);
  }
  const body=n=>JSON.parse(responses[n-2].text);
  const req=n=>requests[n-2];
  const purchases=[body(7),body(14),body(20)];
  const ids=created.map(c=>c.receiptId);
  assert.equal(new Set(ids).size,3); assert.deepEqual(failure.syntheticReceiptIds,ids);
  for(let i=0;i<3;i++) {
    assert.equal(purchases[i].receiptId,ids[i]);
    assert.equal(req([7,14,20][i]).url,`/api/receipts/${ids[i]}/receipt-items`);
    assert.equal(req([7,14,20][i]).method,'POST');
    assert.equal(req([8,15,21][i]).url,`/api/receipts/${ids[i]}/transactions`);
    assert.equal(req([8,15,21][i]).method,'POST');
    assert.equal(purchases[i].normalizationReviewState,'verified');
    assert.equal(purchases[i].normalizedDescriptionId,purchases[0].normalizedDescriptionId);
  }
  assert.equal(purchases[1].normalizationReviewReason,'approved_mapping');
  assert.equal(purchases[2].normalizationReviewReason,'purchase_review');
  const financial=body(24),preview=body(26),rejected=body(27);
  assert.equal(req(23).method,'PUT'); assert.equal(req(23).url,'/api/receipt-items/'+purchases[1].id);
  assert.equal(financial.id,purchases[1].id); assert.equal(financial.quantity,3); assert.equal(financial.unitPrice,2);
  assert.equal(financial.normalizationReviewState,'verified');
  assert.equal(financial.normalizedDescriptionId,purchases[0].normalizedDescriptionId);
  assert.notEqual(financial.normalizationRevision,purchases[1].normalizationRevision);
  assert.equal(req(27).method,'PUT'); assert.equal(req(27).url,'/api/receipt-items/'+financial.id);
  assert.equal(req(27).body.normalizationIntent.expectedItemRevision,purchases[1].normalizationRevision);
  assert.equal(req(27).body.normalizationIntent.kind,'defer');
  assert.equal(req(27).body.normalizationIntent.sourceFingerprint,preview.sourceFingerprint);
  assert.equal(rejected.code,'normalization_item_changed'); assert(rejected.detail);
  assert.equal(preview.targetId,purchases[0].normalizedDescriptionId); assert(preview.mappingId && preview.mappingRevision);
  assert.equal(created[0].intent.kind,'confirmNew'); assert.equal(created[0].intent.rememberSource,true);
  const location=created[0].store,line=created[0].item,tag=line.subcategory;
  assert.equal(location,tag+' store');
  assert.deepEqual(created[1].item,line); assert.deepEqual(created[2].item,line);
  return {manual:{receiptId:ids[0],item:purchases[0]},mapped:{receiptId:ids[1],item:purchases[1]},
    independent:{receiptId:ids[2],item:purchases[2]},financial,p:preview,staleRequest:req(27),
    created:ids,line,location,tag};
}

export function assertSettled(pg) {
  const active=pg.state.identities.filter(i=>i.Status==='Active');
  assert(active.length>0,'No active identity settlement evidence');
  assert(active.every(i=>i.Embedding && i.EmbeddingModelVersion),'Wait for actual embedding projections before measuring atomic rejection');
}
