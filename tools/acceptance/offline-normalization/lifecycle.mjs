import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import assert from 'node:assert/strict';
import { fileURLToPath } from 'node:url';
import { createRequire } from 'node:module';
import { execFileSync } from 'node:child_process';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../../..');
const read = p => JSON.parse(fs.readFileSync(p, 'utf8').replace(/^\uFEFF/, ''));
const hash = p => crypto.createHash('sha256').update(fs.readFileSync(p)).digest('hex');
const cents = n => Math.round(Number(n) * 100);
const rows = value => Array.isArray(value) ? value : value.data;
const uuid = () => crypto.randomUUID();

// Expectations derive from docs/normalization-ownership.md and the public contract.
// These helpers intentionally reject plausible-looking but unauthorized history.
export function assertInvalidated(before, after) {
  assert.equal(after.normalizationReviewState, 'needsReview');
  assert.equal(after.normalizedDescriptionId, null, 'Changed source retained identity');
  assert.notEqual(after.normalizationRevision, before.normalizationRevision);
  assert.equal(cents(after.quantity * after.unitPrice), cents(before.quantity * before.unitPrice));
}
export function assertRevokedHistory(before, after, independent = false) {
  assert.equal(after.normalizedDescriptionId, before.normalizedDescriptionId, 'Revocation rewrote historical identity');
  assert.equal(after.normalizationReviewState, independent ? 'verified' : 'needsReview');
  assert.equal(after.description, before.description);
  assert.equal(cents(after.quantity * after.unitPrice), cents(before.quantity * before.unitPrice));
}
export function assertPreservedRows(baseline, actual) {
  for (const [table, original] of Object.entries(baseline)) {
    const available = new Map();
    for (const row of actual[table] ?? []) {
      const key = JSON.stringify(row); available.set(key, (available.get(key) ?? 0) + 1);
    }
    for (const row of original) {
      const key = JSON.stringify(row), count = available.get(key) ?? 0;
      assert(count > 0, `Original baseline row changed in ${table}`); available.set(key, count - 1);
    }
  }
}
export function assertPrerequisites(records, runId) {
  const {sourceRestore: a, freshRestore: b, sourceRestart: ar, freshRestart: br, followups: f} = records;
  assert.equal(a.target, 'source'); assert.equal(b.target, 'fresh');
  assert.equal(a.runId, runId); assert.notEqual(a.runId, b.runId);
  assert.equal(a.bSha256, b.bSha256); assert.match(a.bSha256, /^[a-f0-9]{64}$/i);
  for (const restore of [a,b]) {
    assert.equal(restore.imports.length, 2); for (const attempt of restore.imports) assert.equal(attempt.status, 200);
    assert(restore.evidence && restore.database, 'Successful semantic/database restore evidence required');
  }
  for (const [restore, restart] of [[a,ar],[b,br]]) {
    assert.equal(restart.runId, restore.runId); assert.equal(restart.phase, 'restart-api');
    assert.equal(restart.restart.Success, true); assert(restart.restart.Before.StartTimeStamp && restart.restart.After.StartTimeStamp);
    assert.notEqual(restart.restart.Before.StartTimeStamp, restart.restart.After.StartTimeStamp);
    assert.deepEqual(restart.before, restart.after); assert(restart.evidence && restart.database);
    assert(Date.parse(restart.utc) >= Date.parse(restore.utc));
  }
  assert.equal(f.runId, runId); assert.equal(f.phase, 'followups'); assert.equal(Object.keys(f.created).length, 7);
  assert(Date.parse(f.utc) >= Math.max(Date.parse(ar.utc), Date.parse(br.utc)), 'Followups must follow both restarts');
  assert.equal(f.evidence.coverage.totalItems, 22); assert.equal(f.evidence.coverage.verifiedItems, 13);
  assert.equal(f.evidence.coverage.pendingItems, 8); assert.equal(f.evidence.coverage.excludedItems, 1);
}

async function main() {
  const args = Object.fromEntries(process.argv.slice(2).map(value => {
    const i = value.indexOf('='); assert(i > 0, 'Arguments must be name=value'); return [value.slice(0,i), value.slice(i+1)];
  }));
  for (const key of ['runtime','out','source','sourceRestore','freshRestore','sourceRestart','freshRestart','followups']) assert(args[key], `${key}=PRIVATE_DIRECTORY required`);
  const runtime = path.resolve(args.runtime), out = path.resolve(args.out);
  const inside = (parent, child) => { const rel = path.relative(parent, child); return !rel.startsWith('..') && !path.isAbsolute(rel); };
  assert(inside(runtime, out) && out !== runtime, 'Output must be below the ACL-restricted runtime');
  assert(!inside(root, out), 'Private evidence must be outside checkout');
  assert(!fs.existsSync(out), 'Use a new evidence directory; failed mutation runs cannot be blindly resumed');
  const readyFiles = fs.readdirSync(runtime).filter(name => name.endsWith('-ready.json'));
  assert.equal(readyFiles.length, 1); const ready = read(path.join(runtime, readyFiles[0]));
  assert.match(ready.RunId, /^receipts-offline-20261024-a-[a-f0-9]{8}$/);
  const names = {sourceRestore:'restore.json',freshRestore:'restore.json',sourceRestart:'after-restart.json',freshRestart:'after-restart.json',followups:'followups.json'};
  const records = Object.fromEntries(Object.entries(names).map(([key,name]) => [key,read(path.join(path.resolve(args[key]),name))]));
  assertPrerequisites(records, ready.RunId);
  assert.equal(path.resolve(records.sourceRestore.runtime), runtime);
  const followupDb = path.join(path.resolve(args.followups), 'after-followups.db');
  const followupHash = hash(followupDb);
  const snapshot = p => JSON.parse(execFileSync('python',[path.join(root,'tools/acceptance/offline-normalization/snapshot.py'),p],{encoding:'utf8',maxBuffer:64*1024*1024,windowsHide:true}));
  const baseline = snapshot(followupDb);
  assert.equal(baseline.tables.receipt_items.length, 22); assert.equal(baseline.tables.receipts.length, 15);
  const bindings = read(path.join(path.resolve(args.source),'bindings.json'));
  const freeze = read(path.join(path.resolve(args.source),'freeze.json'));
  assert.equal(hash(path.join(path.resolve(args.source),'A.db')), freeze.aSha256);
  fs.mkdirSync(out);
  const save = (name,value) => fs.writeFileSync(path.join(out,name), JSON.stringify(value,null,2), {flag:'wx'});
  const stamp = () => ({runId:ready.RunId,utc:new Date().toISOString(),phase:'lifecycle',bSha256:records.sourceRestore.bSha256});
  let token;
  async function request(url, body, method = body === undefined ? 'GET' : 'POST', status) {
    const response = await fetch(new URL(url,ready.Api), {method,headers:{...(token ? {Authorization:`Bearer ${token}`} : {}),...(body === undefined ? {} : {'Content-Type':'application/json'})},body:body === undefined ? undefined : JSON.stringify(body),signal:AbortSignal.timeout(60000)});
    const text = await response.text(); const value = text ? JSON.parse(text) : null;
    if (status !== undefined) { assert.equal(response.status,status, `${method} ${url}`); assert(value?.detail, 'Reasoned rejection must have RFC 9457 detail'); }
    else assert(response.ok, `${method} ${url}: ${response.status}; inspect private evidence/runtime log`);
    return value;
  }
  const secrets = read(path.join(runtime,'secrets.json'));
  const password = read(path.join(runtime,'acceptance-password.json')).password;
  const login = await request('/api/auth/login',{email:secrets.AdminEmail,password});
  assert.equal(login.mustResetPassword,false); token = login.accessToken;
  async function exported(name) {
    const response = await fetch(new URL('/api/backup/export',ready.Api),{method:'POST',headers:{Authorization:`Bearer ${token}`},signal:AbortSignal.timeout(300000)});
    assert(response.ok); const dest = path.join(out,name); fs.writeFileSync(dest,Buffer.from(await response.arrayBuffer()),{flag:'wx'}); return snapshot(dest);
  }
  function postgres() {
    const name = ready.RunId + '-postgres';
    const container = JSON.parse(execFileSync('docker',['inspect',name,'--format','{"Id":{{json .Id}},"Name":{{json .Name}},"Mounts":{{json .Mounts}},"Running":{{json .State.Running}}}'],{encoding:'utf8',windowsHide:true}));
    assert.equal(container.Name,'/'+name); assert.equal(container.Running,true);
    const tables = {items:['receipts','ReceiptItems'],receipts:['receipts','Receipts'],transactions:['receipts','Transactions'],adjustments:['receipts','Adjustments'],identities:['matching','NormalizedDescriptions'],mappings:['matching','NormalizationSourceMappings'],reviewWork:['matching','NormalizationReviewWork'],replays:['receipts','ReceiptWriteRequests'],operations:['matching','NormalizationCatalogOperations'],members:['matching','NormalizationCatalogMembers'],audits:['audit','AuditLogs']};
    // Review work and replay receipts use ItemId/composite keys rather than Id.
    const query = 'BEGIN TRANSACTION ISOLATION LEVEL REPEATABLE READ READ ONLY; SELECT json_build_object(' + Object.entries(tables).map(([key,[schema,table]]) => `'${key}',(SELECT coalesce(json_agg(x ORDER BY to_jsonb(x)::text),'[]') FROM ${schema}."${table}" x)`).join(',') + '); COMMIT;';
    const result = execFileSync('docker',['exec',name,'sh','-c','PGPASSWORD="$POSTGRES_PASSWORD" exec psql "$@"','sh','-U','postgres','-d','receiptsdb','-X','-q','-A','-t','-v','ON_ERROR_STOP=1','-c',query],{encoding:'utf8',maxBuffer:64*1024*1024,windowsHide:true});
    return {container,state:JSON.parse(result)};
  }
  const originalPg = postgres();
  assert.equal(originalPg.state.items.filter(i => !i.DeletedAt).length,22);
  assert.equal(originalPg.state.receipts.filter(i => !i.DeletedAt).length,15);
  assert.deepEqual((await exported('before-lifecycle.db')).tables, baseline.tables, 'Current installation is not the exact seven-followup baseline');
  save('baseline.json',{...stamp(),followupHash,prerequisites:records,postgres:originalPg,snapshot:baseline});
  let step = 0; const completed = [];
  async function checkpoint(name, evidence) {
    const pg = postgres(); assert.equal(pg.container.Id,originalPg.container.Id); assert.deepEqual(pg.container.Mounts,originalPg.container.Mounts);
    for (const key of ['items','receipts','transactions','adjustments','mappings','identities','operations','members']) assertPreservedRows({[key]:originalPg.state[key]},{[key]:pg.state[key]});
    const live = await exported(`${++step}-${name}.db`); assertPreservedRows(baseline.tables,live.tables);
    assert.equal(hash(followupDb),followupHash); assert.equal(hash(path.join(path.resolve(args.source),'A.db')),freeze.aSha256);
    const activeReceiptIds=new Set(pg.state.receipts.filter(r=>!r.DeletedAt).map(r=>r.Id));
    const activeItems=pg.state.items.filter(i=>!i.DeletedAt && activeReceiptIds.has(i.ReceiptId));
    const report=await request('/api/reports/spending-by-normalized-description?pageSize=100');
    const expectedAmount=items=>items.reduce((sum,i)=>sum+cents(Number(i.Quantity)*Number(i.UnitPrice)),0);
    assert.equal(cents(report.grandTotal),expectedAmount(activeItems),'Financial report lost or duplicated live purchases');
    assert.equal(report.items.reduce((sum,i)=>sum+cents(i.totalAmount),0),expectedAmount(activeItems));
    for(const [apiState,dbState] of [['verified','Verified'],['needsReview','NeedsReview'],['excluded','Excluded']]) assert.equal(report.items.filter(i=>i.normalizationReviewState===apiState).reduce((sum,i)=>sum+cents(i.totalAmount),0),expectedAmount(activeItems.filter(i=>i.NormalizationReviewState===dbState)),`Financial review bucket ${apiState}`);
    const coverage=await request('/api/normalization-catalog/coverage'); assert.equal(coverage.totalItems,activeItems.length);
    for(const [field,state] of [['verifiedItems','Verified'],['pendingItems','NeedsReview'],['excludedItems','Excluded']]) assert.equal(coverage[field],activeItems.filter(i=>i.NormalizationReviewState===state).length);
    save(`${step}-${name}.json`,{...stamp(),evidence,postgres:pg,report,coverage}); completed.push(name);
  }
  const getItem = id => request('/api/receipt-items/'+id);
  const trip = id => request('/api/trips?receiptId='+id);
  const source = (location,item) => ({location,description:item.description,itemCode:item.receiptItemCode});
  const preview = (location,item) => request('/api/receipt-items/normalization-preview',{source:source(location,item)});
  const update = (item,changes) => request('/api/receipt-items/'+item.id,{id:item.id,description:item.description,receiptItemCode:item.receiptItemCode,quantity:item.quantity,unitPrice:item.unitPrice,category:item.category,subcategory:item.subcategory,...changes},'PUT');
  const tag = 'Lifecycle '+uuid(); const location = tag+' store';
  const line = {description:tag+' RAW OATS 500G',receiptItemCode:'LIFE-500',quantity:2,unitPrice:3,category:'Groceries',subcategory:tag};
  const created = [];
  async function create(item, intent, store=location) {
    // Deliberately receipt-first, then standalone purchase and payment. No SQL arrangement.
    const receipt = await request('/api/receipts',{location:store,date:'2026-10-20',taxAmount:0});
    created.push(receipt.id); save(`created-${created.length}.json`,{receiptId:receipt.id,store,item,intent});
    const purchase = await request(`/api/receipts/${receipt.id}/receipt-items`,{...item,...(intent ? {normalizationIntent:intent} : {})});
    await request(`/api/receipts/${receipt.id}/transactions`,{cardId:bindings.card,amount:item.quantity*item.unitPrice,date:'2026-10-20'});
    return {receiptId:receipt.id,item:await getItem(purchase.id)};
  }
  async function intentFor(item,kind,extra={},store=location) {const p=await preview(store,item);return {kind,clientRowId:uuid(),sourceFingerprint:p.sourceFingerprint,...extra};}
  async function finances(id, expectedCents=600) {
    const list=rows(await request('/api/receipts?limit=500')); const receipt=list.find(r=>r.id===id); assert(receipt,'Live receipt missing');
    assert.equal(cents(receipt.itemSubtotal),expectedCents); assert.equal(cents(receipt.expectedTotal),expectedCents); assert.equal(cents(receipt.transactionTotal),600);
    return receipt;
  }
  try {
    const availability=await request('/api/normalization-ai/connection'); assert.equal(availability.configured,false,'Use the provider-disabled acceptance runtime');
    const category=rows(await request('/api/categories?limit=500')).find(c=>c.name===line.category && c.isActive); assert(category);
    await request('/api/subcategories',{name:line.subcategory,categoryId:category.id,isActive:true});
    const manual=await create(line,await intentFor(line,'confirmNew',{newName:tag+' Oats 500 g',rememberSource:true}));
    assert.equal(manual.item.normalizationReviewState,'verified'); await finances(manual.receiptId);
    await checkpoint('provider-unavailable-manual-entry',{availability,manual});
    const mapped=await create(line); assert.equal(mapped.item.normalizationReviewState,'verified'); assert.equal(mapped.item.normalizationReviewReason,'approved_mapping');
    const p=await preview(location,line); assert(p.mappingId && p.mappingRevision && p.targetRevision);
    const independent=await create(line,await intentFor(line,'confirmExisting',{targetId:p.targetId,expectedTargetRevision:p.targetRevision,rememberSource:false}));
    assert.equal(independent.item.normalizationReviewReason,'purchase_review');
    // A financial edit changes the revision, but neither product nor authority.
    await update(mapped.item,{quantity:3,unitPrice:2}); const financial=await getItem(mapped.item.id);
    assert.equal(financial.normalizationReviewState,'verified'); assert.equal(financial.normalizedDescriptionId,mapped.item.normalizedDescriptionId); await finances(mapped.receiptId);
    const staleIntent=await intentFor(financial,'defer',{expectedItemRevision:mapped.item.normalizationRevision});
    const rejectBefore=postgres(); const portableBefore=await exported('before-stale-item.db');
    const rejected=await request('/api/receipt-items/'+financial.id,{...line,id:financial.id,description:line.description,unitPrice:91,normalizationIntent:staleIntent},'PUT',409);
    assert.deepEqual(postgres(),rejectBefore,'Rejected stale edit changed durable facts/proof/audits'); assert.deepEqual((await exported('after-stale-item.db')).tables,portableBefore.tables);
    await checkpoint('stale-revision-atomic-rejection',{rejected,financial});
    // Parent source scope is part of a mapped purchase's reviewed fingerprint.
    await request('/api/receipts/'+mapped.receiptId,{id:mapped.receiptId,location:tag+' moved store',date:'2026-10-20',taxAmount:0},'PUT');
    const invalid=await getItem(mapped.item.id); assertInvalidated(financial,invalid); await finances(mapped.receiptId);
    const pgInvalid=postgres().state.items.find(i=>i.Id===invalid.id); const changedPreview=await preview(tag+' moved store',invalid);
    assert.equal(pgInvalid.NormalizationSourceHash,changedPreview.sourceFingerprint); assert.notEqual(pgInvalid.ReviewedSourceHash,pgInvalid.NormalizationSourceHash);
    assert(postgres().state.reviewWork.some(w=>w.ItemId===invalid.id),'Invalidated purchase lacks durable review work');
    await checkpoint('receipt-source-edit-invalidates-authority',{financial,invalid,receipt:await finances(mapped.receiptId)});
    const reused=await create(line); assert.equal(reused.item.normalizationReviewState,'verified');
    const revokeBefore=postgres();
    const staleRevoke=await request('/api/normalization-catalog/mappings/'+p.mappingId+'/revoke',{expectedRevision:uuid()},'POST',409);
    assert.deepEqual(postgres(),revokeBefore,'Rejected mapping revision changed durable state');
    const revoked=await request('/api/normalization-catalog/mappings/'+p.mappingId+'/revoke',{expectedRevision:p.mappingRevision}); assert.equal(revoked.state,'revoked');
    const historical=await getItem(reused.item.id), reviewed=await getItem(independent.item.id);
    assertRevokedHistory(reused.item,historical); assertRevokedHistory(independent.item,reviewed,true);
    const future=await create(line); assert.equal(future.item.normalizationReviewState,'needsReview'); assert.equal(future.item.normalizedDescriptionId,null);
    const staleKnown={kind:'knownSource',clientRowId:uuid(),sourceFingerprint:p.sourceFingerprint,expectedMappingRevision:p.mappingRevision};
    const beforeKnown=postgres(); const beforeKnownPortable=await exported('before-stale-mapping.db');
    const knownRejected=await request('/api/receipts/complete',{clientRequestId:uuid(),receipt:{location,date:'2026-10-20',taxAmount:0},items:[{...line,normalizationIntent:staleKnown}],transactions:[{cardId:bindings.card,amount:6,date:'2026-10-20'}],adjustments:[]},'POST',409);
    assert.deepEqual(postgres(),beforeKnown); assert.deepEqual((await exported('after-stale-mapping.db')).tables,beforeKnownPortable.tables);
    await checkpoint('revoke-history-and-future-reuse',{staleRevoke,revoked,historical,reviewed,future,knownRejected});
    // Two deliberately separate declarations describe the same isolated synthetic product.
    const variant={...line,description:tag+' OATS ALT LABEL',receiptItemCode:'LIFE-ALT'};
    const other=await create(variant,await intentFor(variant,'confirmNew',{newName:tag+' Alternate Oats 500 g',rememberSource:false}));
    assert.notEqual(other.item.normalizedDescriptionId,manual.item.normalizedDescriptionId);
    const merge=await request('/api/normalized-descriptions/'+manual.item.normalizedDescriptionId+'/merge',{discardId:other.item.normalizedDescriptionId});
    const merged=await getItem(other.item.id); assert.equal(merged.normalizedDescriptionId,manual.item.normalizedDescriptionId);
    assert.equal(merged.normalizationReviewState,'verified','Deliberate merge lost current independent purchase review');
    assert.equal(merged.description,variant.description); await finances(other.receiptId);
    const mergePg=postgres(); assert(!mergePg.state.identities.some(i=>i.Id===other.item.normalizedDescriptionId));
    if(merged.normalizationReviewState==='verified') {const row=mergePg.state.items.find(i=>i.Id===merged.id);assert.equal(row.ReviewedTargetId,row.NormalizedDescriptionId);assert.equal(row.NormalizationReviewDecisionId,row.NormalizationAssignmentId);assert.equal(row.ReviewedSourceHash,row.NormalizationSourceHash);}
    await checkpoint('intentional-same-product-merge',{merge,before:other.item,merged});
    // Deleting an entire receipt temporarily removes its purchase and payment from projections.
    const deleteBefore=await trip(independent.receiptId); const reportBefore=await request('/api/reports/spending-by-normalized-description?pageSize=100');
    await request('/api/receipts',[independent.receiptId],'DELETE');
    assert(rows(await request('/api/receipts/deleted?limit=500')).some(r=>r.id===independent.receiptId));
    assert(!rows(await request('/api/receipts?limit=500')).some(r=>r.id===independent.receiptId));
    const deletedPg=postgres(); assert(deletedPg.state.items.find(i=>i.Id===independent.item.id).DeletedAt);
    const reportDeleted=await request('/api/reports/spending-by-normalized-description?pageSize=100'); assert.equal(cents(reportDeleted.grandTotal),cents(reportBefore.grandTotal)-600);
    await request('/api/receipts/'+independent.receiptId+'/restore',{});
    const restoredItem=await getItem(independent.item.id); assertRevokedHistory(independent.item,restoredItem,true); await finances(independent.receiptId);
    assert.equal(cents((await request('/api/reports/spending-by-normalized-description?pageSize=100')).grandTotal),cents(reportBefore.grandTotal));
    await checkpoint('soft-delete-restore',{deleteBefore,restored:await trip(independent.receiptId),reportBefore,reportDeleted});
    await browserDraft(future);
    save('lifecycle.json',{...stamp(),completed,syntheticReceiptIds:created,baselineFollowupHash:followupHash,status:'passed'});
  } catch(error) {
    save('failure.json',{...stamp(),completed,syntheticReceiptIds:created,error:{name:error.name,message:error.message,stack:error.stack}}); throw error;
  }
  async function browserDraft(future) {
    const require=createRequire(path.join(root,'src/client/package.json')); const {chromium,expect}=require('@playwright/test');
    const browser=await chromium.launch({headless:true}); const context=await browser.newContext({baseURL:ready.Frontend});
    await context.addInitScript(({accessToken,refreshToken})=>{localStorage.setItem('receipts_access_token',accessToken);localStorage.setItem('receipts_refresh_token',refreshToken);},login);
    const page=await context.newPage();
    try {
      await page.goto('/receipts/'+future.receiptId);
      // The field fetches on mount; register before opening the editor so a fast
      // response cannot be missed. Bind readiness to this purchase's exact source.
      const previewResponsePromise=page.waitForResponse(response=>{
        if(new URL(response.url()).pathname!=='/api/receipt-items/normalization-preview' || response.request().method()!=='POST' || !response.ok()) return false;
        const body=response.request().postDataJSON();
        return body.source?.location===location && body.source?.description===line.description && body.source?.itemCode===line.receiptItemCode && !body.query && body.offset===0;
      });
      await page.getByRole('row').filter({hasText:line.description}).getByRole('button',{name:'Edit',exact:true}).click();
      const dialog=page.getByRole('dialog',{name:'Edit Item'});
      await dialog.getByLabel('Unit Price',{exact:false}).fill('8.25');
      await dialog.getByRole('button',{name:'Choose product',exact:true}).click();
      const previewResponse=await previewResponsePromise;
      assert.equal(await previewResponse.finished(),null);
      const previewEvidence=await previewResponse.json(); assert(previewEvidence.sourceFingerprint);
      // Review later is enabled even during loading and then clears the choice.
      // Product choices only renders when the successful preview is ready in React.
      await expect(dialog.getByRole('list',{name:'Product choices',exact:true})).toBeAttached();
      await expect(dialog.getByRole('button',{name:'Previous products',exact:true})).toBeVisible();
      await expect(dialog.getByRole('status').filter({hasText:'Loading product choices'})).toBeHidden();
      await dialog.getByRole('button',{name:'Review later',exact:true}).click();
      await expect(dialog.getByText('Needs review — saved with the receipt',{exact:true})).toBeVisible();
      // The explicit choice captures the observed revision before a competing save.
      await update(await getItem(future.item.id),{unitPrice:4});
      const before=postgres(); const portable=await exported('before-browser-rejection.db');
      const responsePromise=page.waitForResponse(r=>new URL(r.url()).pathname==='/api/receipt-items/'+future.item.id && r.request().method()==='PUT');
      await dialog.getByRole('button',{name:'Update Item',exact:true}).click();
      const response=await responsePromise; assert.equal(response.status(),409,'Expected genuine stale-revision server rejection');
      const body=response.request().postDataJSON(); assert.equal(cents(body.unitPrice),825); assert.equal(body.normalizationIntent?.kind,'defer'); assert.equal(body.normalizationIntent?.expectedItemRevision,future.item.normalizationRevision); assert.equal(body.normalizationIntent?.sourceFingerprint,previewEvidence.sourceFingerprint);
      const problem=await response.json(); assert(problem.detail);
      await expect(dialog).toBeVisible(); await expect(dialog.getByRole('alert')).toContainText(problem.detail);
      await expect(dialog.getByLabel('Unit Price',{exact:false})).toHaveValue('8.25');
      assert.deepEqual(postgres(),before); assert.deepEqual((await exported('after-browser-rejection.db')).tables,portable.tables);
      const refresh=page.waitForResponse(r=>new URL(r.url()).pathname==='/api/trips' && r.request().method()==='GET' && r.ok());
      await dialog.getByRole('button',{name:'Refresh saved receipt information',exact:true}).click();
      const refreshed=await (await refresh).json(); assert.equal(cents(refreshed.receipt.items.find(i=>i.id===future.item.id).unitPrice),400);
      await expect(dialog).toBeVisible(); await expect(dialog.getByLabel('Unit Price',{exact:false})).toHaveValue('8.25');
      await page.screenshot({path:path.join(out,'browser-draft-after-refresh.png'),fullPage:true});
      await dialog.getByRole('button',{name:'Cancel',exact:true}).click();
      await expect(page.getByRole('row').filter({hasText:line.description})).toContainText('$4.00');
      await checkpoint('browser-rejection-cache-refresh-draft',{request:body,status:response.status(),problem,refreshed,interception:false});
    } catch(error) {await page.screenshot({path:path.join(out,'browser-failure.png'),fullPage:true});throw error;} finally {await browser.close();}
  }
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) await main();
