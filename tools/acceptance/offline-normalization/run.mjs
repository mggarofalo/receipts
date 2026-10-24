import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
import { fileURLToPath } from 'node:url';
import { execFileSync } from 'node:child_process';
import { assertIdentities, assertHistory, assertFacts, assertInitial, assertPortableGold, assertOperationArchiveUnchanged, assertRestoredOperations, assertRepeatedRestore } from './oracle.mjs';
import { retainedRestoreUploads, assertFrozenInputs } from './restore-resume.mjs';

// This runner is the independent oracle. Never pass its gold or bindings to a proposer.
const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../../..');
const args = Object.fromEntries(process.argv.slice(3).map(v => { const i = v.indexOf('='); return [v.slice(0,i),v.slice(i+1)]; }));
const phase = process.argv[2];
if (!phase || !args.runtime || !args.out) throw Error('Usage: node run.mjs PHASE runtime=PRIVATE_RUNTIME_DIR out=PRIVATE_EVIDENCE_DIR [source=SOURCE_EVIDENCE_DIR b=EXACT_B.db sha256=HASH target=source|fresh]');
const runtime = path.resolve(args.runtime), out = path.resolve(args.out);
assert(!out.toLowerCase().startsWith(root.toLowerCase() + path.sep), 'Evidence must be outside the repository');
fs.mkdirSync(out,{recursive:true});
if(phase==='restore-resume')assert.equal(fs.readdirSync(out).length,0,'Resume output must be a new empty evidence directory');
const gold = JSON.parse(fs.readFileSync(path.join(root,'test-data/offline-normalization-acceptance.json')));
const read = p => JSON.parse(fs.readFileSync(p,'utf8').replace(/^\uFEFF/,''));
const save = (name,v) => fs.writeFileSync(path.join(out,name),JSON.stringify(v,null,2),{flag:'wx'});
const readyFiles = fs.readdirSync(runtime).filter(n=>n.endsWith('-ready.json'));
assert.equal(readyFiles.length,1,'Exactly one task runtime ready manifest required');
const ready = read(path.join(runtime,readyFiles[0]));
assert.match(ready.RunId,/^receipts-offline-20261024-[ab]-[a-f0-9]{8}$/);
const secrets = read(path.join(runtime,'secrets.json'));
const source = path.resolve(args.source ?? out);
const stamp=()=>({runId:ready.RunId,runtime,revision:read(path.join(runtime,'source.json')),utc:new Date().toISOString(),phase});
const hash = p => crypto.createHash('sha256').update(fs.readFileSync(p)).digest('hex');
const facts = p => JSON.parse(execFileSync('python',[path.join(root,'tools/acceptance/offline-normalization/snapshot.py'),p],{encoding:'utf8'}));
let token;
const requests=[];
const recordResponse=(method,url,r)=>requests.push({utc:new Date().toISOString(),method,url,status:r.status});
const pause=ms=>new Promise(resolve=>setTimeout(resolve,ms));
const durableSave=(name,value)=>{const fd=fs.openSync(path.join(out,name),'wx');try{fs.writeFileSync(fd,JSON.stringify(value,null,2));fs.fsyncSync(fd);}finally{fs.closeSync(fd);}};
let lastRequest=0;
async function api(url,body,method=body===undefined?'GET':'POST') {
  if(phase==='followups'){await pause(Math.max(0,lastRequest+850-Date.now()));lastRequest=Date.now();}
  const r = await fetch(new URL(url,ready.Api),{method,headers:{...(token?{Authorization:`Bearer ${token}`} : {}),...(body === undefined ? {} : {'Content-Type':'application/json'})},body:body===undefined?undefined:JSON.stringify(body),signal:AbortSignal.timeout(300000)});
  recordResponse(method,url,r);
  if(!r.ok) throw Error(`${method} ${url}: ${r.status}; inspect private runtime log`);
  return r.status===204 ? null : r.json();
}
async function authenticate() {
  const saved = path.join(runtime,'acceptance-password.json');
  let password = fs.existsSync(saved)?read(saved).password:secrets.AdminPassword;
  let login = await api('/api/auth/login',{email:secrets.AdminEmail,password});
  token=login.accessToken;
  if(login.mustResetPassword) {
    assert.notEqual(phase,'restore-resume','Read-only resume cannot change a password');
    const replacement = crypto.randomBytes(32).toString('hex')+'aA1!';
    await api('/api/auth/change-password',{currentPassword:password,newPassword:replacement});
    fs.writeFileSync(saved,JSON.stringify({password:replacement}),{flag:'wx'});
    login=await api('/api/auth/login',{email:secrets.AdminEmail,password:replacement}); token=login.accessToken;
  }
  return login;
}
async function exportDb(name) {
  const r=await fetch(new URL('/api/backup/export',ready.Api),{method:'POST',headers:{Authorization:`Bearer ${token}`},signal:AbortSignal.timeout(300000)});
  recordResponse('POST','/api/backup/export',r);
  assert(r.ok,`Export ${r.status}`); const dest=path.join(out,name);
  fs.writeFileSync(dest,Buffer.from(await r.arrayBuffer()),{flag:'wx'}); return dest;
}
async function importDb(p,expected) {
  assert.equal(hash(p),expected,'Exact B hash changed');
  const data=new FormData();data.append('file',new Blob([fs.readFileSync(p)]),'backup.db');
  const r=await fetch(new URL('/api/backup/import',ready.Api),{method:'POST',headers:{Authorization:`Bearer ${token}`},body:data,signal:AbortSignal.timeout(300000)});
  assert.equal(hash(p),expected);return {status:r.status,body:await r.json()};
}
async function rejectedAction(url,body) {
  const response=await fetch(new URL(url,ready.Api),{method:'POST',headers:{Authorization:`Bearer ${token}`,'Content-Type':'application/json'},body:JSON.stringify(body),signal:AbortSignal.timeout(300000)});
  recordResponse('POST',url,response);
  const problem=await response.json();assert.equal(response.status,409,'Restored operation accepted a mutation');assert(problem.detail);return {status:response.status,problem};
}
function postgresEvidence() {
  const name=ready.RunId+'-postgres';
  const inspected=JSON.parse(execFileSync('docker',['inspect',name,'--format','{"Id":{{json .Id}},"Name":{{json .Name}},"State":{"Running":{{json .State.Running}}},"Mounts":{{json .Mounts}}}'],{encoding:'utf8'}));
  assert.equal(inspected.Name,'/'+name);assert(inspected.State.Running,'Task PostgreSQL must remain running');
  const query=`SELECT json_build_object('items',(SELECT coalesce(json_agg(x ORDER BY x."Id"),'[]') FROM receipts."ReceiptItems" x WHERE x."DeletedAt" IS NULL),'receipts',(SELECT coalesce(json_agg(x ORDER BY x."Id"),'[]') FROM receipts."Receipts" x WHERE x."DeletedAt" IS NULL),'identities',(SELECT coalesce(json_agg(x ORDER BY x."Id"),'[]') FROM matching."NormalizedDescriptions" x),'mappings',(SELECT coalesce(json_agg(x ORDER BY x."Id"),'[]') FROM matching."NormalizationSourceMappings" x),'operations',(SELECT coalesce(json_agg(x ORDER BY x."Id"),'[]') FROM matching."NormalizationCatalogOperations" x),'members',(SELECT coalesce(json_agg(x ORDER BY x."Id"),'[]') FROM matching."NormalizationCatalogMembers" x));`;
  const state=JSON.parse(execFileSync('docker',['exec',name,'sh','-c','PGPASSWORD="$POSTGRES_PASSWORD" exec psql "$@"','sh','-U','postgres','-d','receiptsdb','-X','-A','-t','-v','ON_ERROR_STOP=1','-c',query],{encoding:'utf8',maxBuffer:16*1024*1024}));
  return {containerId:inspected.Id,mounts:inspected.Mounts.map(({Type,Name,Source,Destination})=>({Type,Name,Source,Destination})),state};
}
async function assertDatabase(bindings,identities) {
  const pg=postgresEvidence();
  for(const r of gold.receipts)for(const expected of r.items) {
    const item=pg.state.items.find(i=>i.Id===bindings.items[expected.key]);assert(item,'Missing real PostgreSQL item');
    assert.equal(item.Description,expected.description);assert.equal(item.ReceiptItemCode,expected.receiptItemCode);assert.equal(Number(item.Quantity),expected.quantity);assert.equal(Number(item.UnitPrice),expected.unitPrice);assert.equal(item.ReceiptId,bindings.receipts[r.key]);
    assert.match(item.NormalizationRevision,/^[a-f0-9-]{36}$/);assert.notEqual(item.NormalizationRevision,'00000000-0000-0000-0000-000000000000');
    const preview=await api('/api/receipt-items/normalization-preview',{source:{location:r.location,description:expected.description,itemCode:expected.receiptItemCode}});
    assert.equal(item.NormalizationSourceHash,preview.sourceFingerprint,'Current source fingerprint');
    if(item.NormalizationReviewState!=='NeedsReview') {
      assert.equal(item.ReviewedSourceHash,preview.sourceFingerprint);assert.equal(item.ReviewedTargetId,item.NormalizedDescriptionId);assert.equal(item.NormalizationReviewDecisionId,item.NormalizationAssignmentId);assert(item.ReviewedBy&&item.ReviewedAt);
      if(item.NormalizationReviewReason==='approved_mapping') {const mapping=pg.state.mappings.find(m=>m.Id===item.NormalizationMappingId);assert(mapping);assert.equal(mapping.Revision,item.ReviewMappingRevision);assert.equal(mapping.SourceHash,preview.sourceFingerprint);}
    }
  }
  const active=pg.state.mappings.filter(m=>m.State==='active');assert.equal(active.length,gold.gold.rememberedSources.length,'Unexpected reusable rules');
  for(const expected of gold.gold.rememberedSources) {
    const receipt=gold.receipts.find(r=>r.items.some(i=>i.key===expected.itemKey));const item=receipt.items.find(i=>i.key===expected.itemKey);
    const matches=active.filter(m=>m.Location===receipt.location.trim()&&m.Description===item.description.replace(/^ +| +$/g,'')&&m.ItemCode===item.receiptItemCode);assert.equal(matches.length,1,'Missing exact scoped rule');assert.equal(matches[0].TargetId,identities[expected.identityKey]);assert.equal(matches[0].Exclude,false);assert(matches[0].ApprovedBy&&matches[0].ApprovedAt);
  }
  const operations=unwrap(await api('/api/normalization-catalog/operations'));const rejected=[];
  assert(bindings.operation&&pg.state.operations.some(op=>op.Id===bindings.operation),'Genuine portable operation fixture is missing');
  for(const op of pg.state.operations) {assert.equal(op.State,'restored');assert.equal(op.ErrorCode,'restored_observation');const visible=operations.find(o=>o.id===op.Id)??await api('/api/normalization-catalog/operations/'+op.Id);assert.equal(visible.state,'restored');for(const action of ['approve','resume','cancel','undo']) rejected.push(await rejectedAction(`/api/normalization-catalog/operations/${op.Id}/${action}`,{expectedRevision:visible.revision}));}
  assert.deepEqual(postgresEvidence(),pg,'Inspect-only checks changed PostgreSQL');
  return {postgres:pg,operations,mutationRejections:rejected,portableOperations:pg.state.operations.length,operationGate:pg.state.operations.length?'exercised':'no portable operations; separate genuine operation fixture still required'};
}
function unwrap(v){return Array.isArray(v)?v:v.data??v.items??v;}
const money=n=>Math.round(Number(n)*100);
async function browser(login,fn) {
  const require=createRequire(path.join(root,'src/client/package.json'));
  const {chromium,expect}=require('@playwright/test');
  const b=await chromium.launch({headless:true});
  const context=await b.newContext({baseURL:ready.Frontend});
  await context.addInitScript(({accessToken,refreshToken})=>{localStorage.setItem('receipts_access_token',accessToken);localStorage.setItem('receipts_refresh_token',refreshToken);},login);
  const page=await context.newPage();
  try {await fn(page,expect);} catch(e) {await page.screenshot({path:path.join(out,`failure-${Date.now()}.png`),fullPage:true});throw e;} finally {await b.close();}
}
async function select(page,label,value,{allowLocationCreation=false}={}) {
  await page.getByRole('combobox',{name:label}).click();
  // Card options also render their last four digits in the accessible name.
  // Filtering preserves strict uniqueness while allowing that real UI detail.
  const option=page.getByRole('option').filter({hasText:value});
  if(!allowLocationCreation) {await option.click();return;}
  if(await option.count()) await option.click();
  else {await page.getByPlaceholder('Search locations...').fill(value);await page.getByRole('button',{name:`Use "${value}"`,exact:true}).click();}
}
function itemBody(i,bindings){return {description:i.description,receiptItemCode:i.receiptItemCode,quantity:i.quantity,unitPrice:i.unitPrice,category:i.category??'Groceries',...(i.itemTemplateKey?{itemTemplateId:bindings.template}: {})};}
async function apiReceipt(r,bindings,fragments=true,followupKey) {
  const items=[];
  for(const i of r.items) {
    const body=itemBody(i,bindings);
    if(fragments && i.fragmentName) {
      const preview=await api('/api/receipt-items/normalization-preview',{source:{location:r.location,description:i.description,itemCode:i.receiptItemCode}});
      body.normalizationIntent={kind:'confirmNew',clientRowId:crypto.randomUUID(),newName:i.fragmentName,sourceFingerprint:preview.sourceFingerprint,rememberSource:false};
    }
    items.push(body);
  }
  const receipt={location:r.location,date:r.date,taxAmount:r.taxAmount??0};
  const transactions=[{cardId:bindings.card,amount:r.transactionAmount??r.quantity*r.unitPrice,date:r.date}];
  const adjustments=(r.adjustments??[]).map(({key,...v})=>v);
  if(r.entry==='api-receipt-then-items') {
    const created=await api('/api/receipts',receipt), id=created.id;
    for(const item of items) await api(`/api/receipts/${id}/receipt-items`,item);
    for(const transaction of transactions) await api(`/api/receipts/${id}/transactions`,transaction);
    for(const adjustment of adjustments) await api(`/api/receipts/${id}/adjustments`,adjustment);
    return id;
  }
  if(followupKey) {
    const requestName=`followup-${followupKey}-request.json`,responseName=`followup-${followupKey}-response.json`;
    const requestPath=path.join(out,requestName),responsePath=path.join(out,responseName);
    if(!fs.existsSync(requestPath))durableSave(requestName,{...stamp(),key:followupKey,body:{clientRequestId:crypto.randomUUID(),receipt,items,transactions,adjustments}});
    const retained=read(requestPath);assert.equal(retained.runId,ready.RunId);assert.equal(retained.key,followupKey);
    assert.deepEqual({...retained.body,clientRequestId:null},{clientRequestId:null,receipt,items,transactions,adjustments});
    let result;
    if(fs.existsSync(responsePath))result=read(responsePath);
    else {
      await pause(Math.max(0,lastRequest+850-Date.now()));lastRequest=Date.now();
      const response=await fetch(new URL('/api/receipts/complete',ready.Api),{method:'POST',headers:{Authorization:`Bearer ${token}`,'Content-Type':'application/json'},body:JSON.stringify(retained.body),signal:AbortSignal.timeout(300000)});
      result={...stamp(),clientRequestId:retained.body.clientRequestId,status:response.status,text:await response.text()};
      durableSave(responseName,result);recordResponse('POST','/api/receipts/complete',response);
    }
    assert.equal(result.runId,ready.RunId);assert.equal(result.clientRequestId,retained.body.clientRequestId);assert(result.status>=200&&result.status<300,`Followup ${followupKey}: ${result.status}; retained private response`);
    return JSON.parse(result.text).receipt.id;
  }
  return (await api('/api/receipts/complete',{clientRequestId:crypto.randomUUID(),receipt,items,transactions,adjustments})).receipt.id;
}
async function bindReceipt(r,id,bindings) {
  bindings.receipts[r.key]=id;
  const trip=await api(`/api/trips?receiptId=${id}`);
  const actual=trip.receipt.items;
  assert.equal(actual.length,r.items.length);
  r.items.forEach(i=>{const matches=actual.filter(a=>a.description===i.description&&Number(a.unitPrice)===Number(i.unitPrice)&&Number(a.quantity)===Number(i.quantity));assert.equal(matches.length,1,'Ambiguous binding');bindings.items[i.key]=matches[0].id;});
  return trip;
}
async function assertGold(bindings,followups=false) {
  const trips={}; const items={};
  for(const r of gold.receipts) {
    const trip=await api(`/api/trips?receiptId=${bindings.receipts[r.key]}`); trips[r.key]=trip;
    for(const i of trip.receipt.items)items[i.id]=i;
  }
  const identities=assertIdentities(gold,bindings,items);
  for(const key of gold.gold.verifiedItems)assert.equal(items[bindings.items[key]].normalizationReviewState,'verified');
  for(const key of gold.gold.needsReviewItems)assert.equal(items[bindings.items[key]].normalizationReviewState,'needsReview');
  for(const {key} of gold.gold.excludedItems) {const i=items[bindings.items[key]];assert.equal(i.normalizationReviewState,'excluded');assert(i.normalizationExclusionReason);}
  const coverage=await api('/api/normalization-catalog/coverage');
  const counts=followups?gold.afterFollowups:gold.gold.counts;
  for(const [k,v]of Object.entries({totalItems:counts.items,verifiedItems:counts.verifiedItems,pendingItems:counts.needsReviewItems,excludedItems:counts.excludedItems}))assert.equal(coverage[k],v,`Coverage ${k}`);
  const report=await api('/api/reports/spending-by-normalized-description?pageSize=100');
  const financial=followups?gold.afterFollowups.USD:gold.gold.financial.USD;
  assert.equal(money(report.grandTotal),money(financial.itemSubtotal),'Report USD total');
  assert.equal(report.items.reduce((sum,row)=>sum+money(row.totalAmount),0),money(financial.itemSubtotal),'Report page omitted money');
  for(const row of report.items)assert.equal(row.currency,'USD','Unexpected currency');
  for(const [state,expected]of [['verified',financial.verifiedItemSubtotal],['needsReview',financial.needsReviewItemSubtotal],['excluded',financial.excludedItemSubtotal]])assert.equal(report.items.filter(i=>i.normalizationReviewState===state).reduce((sum,i)=>sum+money(i.totalAmount),0),money(expected),'Report review bucket '+state);
  if(!followups) for(const group of gold.gold.identities) {const rows=report.items.filter(i=>i.normalizedDescriptionId===identities[group.key]);assert.equal(rows.length,1);assert.equal(money(rows[0].totalAmount),money(gold.gold.reportGroupSubtotals[group.key]));}
  const mappings={};
  for(const r of gold.receipts)for(const i of r.items) {
    const params=new URLSearchParams({location:r.location,description:i.description,...(i.receiptItemCode===null?{}:{itemCode:i.receiptItemCode})});
    mappings[i.key]=await api('/api/normalization-catalog/mappings?'+params);
  }
  const sources=await api('/api/normalization-catalog/sources?limit=100');
  const history=await api('/api/reports/item-cost-over-time?normalizedDescription='+encodeURIComponent(gold.gold.identities[0].name)+'&granularity=exact');
  if(!followups)assert.deepEqual(history.buckets.map(b=>b.period.slice(0,10)),gold.gold.beefPriceHistory.map(h=>h.date));
  const searches={};
  for(const [q,keys] of [['ACME BEEF FRNK 12OZ',['r1-beef','r3-code']],['Acme Beef Franks 12 oz',gold.gold.identities[0].members]]) {const result=await api('/api/search/purchases?q='+encodeURIComponent(q)+'&limit=100&sortBy=date&sortDirection=asc');for(const key of keys)assert(result.items.some(i=>i.id===bindings.items[key]),'Raw/normalized search missed expected purchase '+key);searches[q]=result;}
  const purchaseHistory=await api('/api/search/purchases?normalizedDescriptionId='+identities.beef12+'&limit=100&sortBy=date&sortDirection=asc');
  if(!followups)assertHistory(gold,bindings,purchaseHistory.items);
  return {trips,coverage,report,mappings,sources,history,purchaseHistory,searches,identities};
}
async function verifyBrowser(login,bindings) {
  // Production defaults: 100 requests/minute in four sliding segments. Pages issue
  // multiple real API requests; allow a full window after dense API assertions.
  await pause(65000);
  await browser(login,async(page,expect)=>{
    const network=[];page.on('response',r=>{if(new URL(r.url()).pathname.startsWith('/api/'))network.push({utc:new Date().toISOString(),url:r.url(),status:r.status()});});
    let lastNavigation=0;
    const pace=async()=>{await pause(Math.max(0,12000-(Date.now()-lastNavigation)));lastNavigation=Date.now();};
    const navigate=async url=>{await pace();return page.goto(url);};
    try {
    for(const r of gold.receipts.slice(0,7)) {
      await navigate(`/receipts/${bindings.receipts[r.key]}`);
      for(const i of r.items)await expect(page.getByText(i.description.trim(),{exact:true}).first()).toBeVisible();
      const verified=r.items.filter(i=>gold.gold.verifiedItems.includes(i.key)).length,pending=r.items.filter(i=>gold.gold.needsReviewItems.includes(i.key)).length,excluded=r.items.length-verified-pending;
      await expect(page.getByLabel('Purchase verification coverage')).toHaveText(`Purchase review: ${verified} verified · ${pending} needs review · ${excluded} excluded`);
      await page.screenshot({path:path.join(out,`receipt-${r.key}-${Date.now()}.png`),fullPage:true});
    }
    await navigate('/receipts');await expect(page.getByRole('main')).toBeVisible();
    for(const r of gold.receipts.slice(0,7)) {const row=page.getByRole('row').filter({has:page.locator(`a[href="/receipts/${bindings.receipts[r.key]}"]`)});await expect(row).toHaveCount(1);await expect(row).toContainText(/verified/);}
    await page.screenshot({path:path.join(out,`list-${Date.now()}.png`),fullPage:true});
    for(const q of ['ACME BEEF FRNK 12OZ','Acme Beef Franks 12 oz']) {await navigate('/search');await page.getByPlaceholder('Item, item code, normalized name, or store').fill(q);await page.getByRole('button',{name:'Search',exact:true}).click();await expect(page.getByRole('table',{name:'Purchase results'})).toBeVisible();await expect(page.locator(`a[href="/receipts/${bindings.receipts.r1}"]`).filter({hasText:'View receipt'})).toBeVisible();await page.screenshot({path:path.join(out,`search-${Date.now()}.png`),fullPage:true});}
    await pause(65000);const evidence=await assertGold(bindings);await pause(65000);await navigate('/search?normalizedDescriptionId='+evidence.identities.beef12+'&sortBy=date&sortDirection=asc&limit=100');
    const rows=page.getByRole('table',{name:'Purchase results'}).locator('tbody tr');await expect(rows).toHaveCount(gold.gold.beefPriceHistory.length);
    for(const [n,expected] of gold.gold.beefPriceHistory.entries()) {await expect(rows.nth(n)).toContainText(Number(expected.unitPrice).toFixed(2));await expect(rows.nth(n).getByRole('link',{name:'View receipt'})).toHaveAttribute('href','/receipts/'+bindings.receipts[expected.itemKey.split('-')[0]]);await expect(rows.nth(n)).toContainText('Verified');}
    await pace();await rows.first().getByRole('link',{name:'View receipt'}).click();await expect(page).toHaveURL(new RegExp('/receipts/'+bindings.receipts.r1+'$'));
    await navigate('/reports?report=spending-by-normalized-description&startDate=2026-09-01&endDate=2026-09-30&pageSize=100');
    const productTable=page.getByRole('table').filter({has:page.getByRole('columnheader',{name:/^Canonical Name/})});
    await expect(productTable).toHaveCount(1);
    for(const group of gold.gold.identities) {const row=productTable.getByRole('row').filter({has:page.getByRole('link',{name:group.name,exact:true})});await expect(row).toHaveCount(1);await expect(row).toContainText(Number(gold.gold.reportGroupSubtotals[group.key]).toFixed(2));}
    await page.screenshot({path:path.join(out,`report-${Date.now()}.png`),fullPage:true});
    assert.deepEqual(network.filter(r=>r.status>=400),[],'Browser API request failed');
    } finally {save(`browser-network-${Date.now()}.json`,network);}
  });
}
let checkpoint='authentication';
try {
const login=await authenticate();
if(phase==='arrange') {
  assert.equal(unwrap(await api('/api/receipts?limit=500')).length,0,'Arrange requires empty task database');
  const bindings={receipts:{},items:{}};
  bindings.account=(await api('/api/accounts',gold.account.request)).id;
  bindings.card=(await api('/api/cards',{...gold.card.request,accountId:bindings.account})).id;
  bindings.template=(await api('/api/item-templates',gold.template.request)).id;
  const categories=unwrap(await api('/api/categories?limit=500'));
  for(const name of new Set(gold.receipts.flatMap(r=>r.items.map(i=>i.category))))if(!categories.some(c=>c.name===name))await api('/api/categories',{name,isActive:true});
  // Real form events; no route interception, injected draft, or browser-side API create.
  const r=gold.receipts[0];
  await browser(login,async(page,expect)=>{
    await page.goto('/receipts/new');
    await select(page,/^Location/,r.location,{allowLocationCreation:true});
    await page.getByPlaceholder('MM/DD/YYYY').first().fill('09/01/2026');
    for(const i of r.items) {
      await page.getByPlaceholder('Item description').fill(i.description);
      await page.locator('[data-slot="form-item"]').filter({has:page.locator('label').filter({hasText:/^Item Code$/})}).getByRole('combobox').fill(i.receiptItemCode);
      await select(page,/^Category/,i.category);
      await page.getByLabel('Unit Price',{exact:false}).fill(String(i.unitPrice));
      if(i.fragmentName) {await page.getByRole('button',{name:'Choose product',exact:true}).click();await page.getByLabel('New product name').fill(i.fragmentName);await page.getByRole('button',{name:'New product and use it'}).click();}
      await page.getByRole('button',{name:'Add Item',exact:true}).click();
    }
    await select(page,/^Account/,gold.account.request.name);
    await select(page,/^Card/,gold.card.request.name);
    await page.getByRole('textbox',{name:/^Amount/}).fill(String(r.transactionAmount));
    await page.getByPlaceholder('MM/DD/YYYY').last().fill('09/01/2026');
    await page.getByRole('button',{name:'Add',exact:true}).click();
    const saved=page.waitForResponse(res=>res.url().endsWith('/api/receipts/complete')&&res.request().method()==='POST');
    await page.getByRole('button',{name:'Submit Receipt',exact:true}).first().click();
    const response=await saved;assert(response.ok(),'Browser receipt rejected');
    const body=await response.json();await bindReceipt(r,body.receipt.id,bindings);
    await expect(page).toHaveURL(new RegExp('/receipts/'+body.receipt.id+'$'));
    await page.screenshot({path:path.join(out,'arranged-r1.png'),fullPage:true});
  });
  for(const receipt of gold.receipts.slice(1))await bindReceipt(receipt,await apiReceipt(receipt,bindings),bindings);
  const initialItems={};
  for(const receipt of gold.receipts) {
    const trip=await api(`/api/trips?receiptId=${bindings.receipts[receipt.key]}`);
    for(const item of trip.receipt.items)initialItems[item.id]=item;
  }
  assertInitial(gold,bindings,initialItems);save('initial-state.json',initialItems);
  // A genuine unapproved observation: no target creation, mapping, exclusion or worker propagation.
  const previewSource=gold.receipts.find(r=>r.key==='r7'),previewItem=previewSource.items[0];
  const beforePreview=postgresEvidence();assert.equal(beforePreview.state.operations.length,0,'Fresh arrangement has historical operations');
  const operationRequest={clientRequestId:crypto.randomUUID(),source:{location:previewSource.location,description:previewItem.description,itemCode:previewItem.receiptItemCode},exclude:true,includeLegacyUnknown:false};
  const operation=await api('/api/normalization-catalog/operations',operationRequest);
  assert.equal(operation.state,'preview');assert.equal(operation.total,1);assert.equal(operation.eligible,1);assert.equal(operation.applied,0);assert.equal(operation.mappingId,null);assert.equal(operation.targetId,null);
  const afterPreview=postgresEvidence();assert.equal(afterPreview.state.operations.length,1);assert.equal(afterPreview.state.operations[0].Id,operation.id);
  assert.equal(beforePreview.state.members.length,0);assert.equal(afterPreview.state.members.length,1);
  const member=afterPreview.state.members[0];assert.equal(member.OperationId,operation.id);assert.equal(member.ItemId,bindings.items[previewItem.key]);assert.equal(member.ReceiptId,bindings.receipts[previewSource.key]);assert.equal(member.State,'pending');assert.equal(member.AppliedRevision,null);assert.equal(member.AssignmentId,null);
  assert.deepEqual({...afterPreview.state,operations:[],members:[]},{...beforePreview.state,operations:[],members:[]},'Unapproved preview changed business state');
  save('operation-fixture.json',{request:operationRequest,response:operation,postgres:afterPreview.state.operations,members:afterPreview.state.members});bindings.operation=operation.id;
  save('bindings.json',bindings);
  const a=await exportDb('A.db');const snapshot=facts(a);save('baseline.json',snapshot);
  assert.equal(snapshot.tables.receipts.length,gold.gold.counts.receipts);assert.equal(snapshot.tables.receipt_items.length,gold.gold.counts.items);
  assert.equal(snapshot.tables.transactions.reduce((s,t)=>s+money(t.amount),0),money(gold.gold.financial.USD.transactionTotal));
  save('freeze.json',{...stamp(),aSha256:hash(a),goldSha256:hash(path.join(root,'test-data/offline-normalization-acceptance.json')),freeze:'No business data writes until exact B restore',counts:gold.gold.counts});
  fs.chmodSync(a,0o444);
} else if(phase==='freeze-check') {
  const freeze=read(path.join(source,'freeze.json'));assert.equal(hash(path.join(source,'A.db')),freeze.aSha256);
  const current=await exportDb('freeze-check.db');assert.deepEqual(facts(current).tables,read(path.join(source,'baseline.json')).tables,'Source changed during freeze');save('freeze-check.json',stamp());
} else if(phase==='restore') {
  assert(['source','fresh'].includes(args.target));assert(args.b&&args.sha256,'Explicit B and recorded offline hash required');
  const freeze=read(path.join(source,'freeze.json'));assert.equal(hash(path.join(source,'A.db')),freeze.aSha256);
  const before=await exportDb('before-restore.db');
  if(args.target==='source')assert.deepEqual(facts(before).tables,read(path.join(source,'baseline.json')).tables,'Source is not frozen');
  else assert.equal(facts(before).tables.receipts.length,0,'Fresh target contains receipts');
  const b=path.resolve(args.b);assert.equal(hash(b),args.sha256,'Exact B hash changed');const inspected=facts(b);assertFacts(inspected.facts,read(path.join(source,'baseline.json')).facts);
  assertOperationArchiveUnchanged(inspected.tables,read(path.join(source,'baseline.json')).tables);
  const bindings=read(path.join(source,'bindings.json'));const preflight=assertPortableGold(gold,bindings,inspected);save('pre-import-gold.json',{bSha256:hash(b),identities:preflight});
  const imports=[];for(let n=1;n<=2;n++){
    save(`import-${n}-attempt.json`,{...stamp(),bPath:b,bSha256:hash(b),target:args.target});
    const result=await importDb(b,args.sha256);
    save(`import-${n}-response.json`,{...stamp(),bPath:b,bSha256:hash(b),target:args.target,...result});
    assert.equal(result.status,200,'Restore rejected');imports.push(result);const exported=await exportDb(`restored-${n}.db`),snapshot=facts(exported);assert.deepEqual(snapshot.facts,inspected.facts);assertRestoredOperations(snapshot.tables,inspected.tables);if(n===2)assertRepeatedRestore(snapshot.tables,facts(path.join(out,'restored-1.db')).tables);
  }
  const evidence=await assertGold(bindings);await verifyBrowser(login,bindings);const database=await assertDatabase(bindings,evidence.identities);
  save('restore.json',{...stamp(),target:args.target,bPath:b,bSha256:args.sha256,imports,evidence,database,pending:['API-only restart phase','edit/revoke/merge/delete-restore and draft refresh','negative-copy and sensitivity phases',...(database.portableOperations?[]:['genuine portable historical operation fixture'])]});
} else if(phase==='restore-resume') {
  checkpoint='retained-upload-and-artifact-validation';
  assert(args.prior&&args.b&&args.sha256,'prior=original restore attempt, b=exact B and sha256=recorded hash required');
  const prior=path.resolve(args.prior),b=path.resolve(args.b);
  assert.notEqual(prior.toLowerCase(),out.toLowerCase(),'Resume must preserve original evidence');
  const uploads=retainedRestoreUploads({prior,runtime,runId:ready.RunId,target:args.target,b,sha256:args.sha256});
  const freeze=read(path.join(source,'freeze.json')),baseline=read(path.join(source,'baseline.json'));
  const frozenInputs={aPath:path.join(source,'A.db'),goldPath:path.join(root,'test-data/offline-normalization-acceptance.json'),freeze};
  const frozenHashes=assertFrozenInputs(frozenInputs);
  assert.deepEqual(facts(path.join(source,'A.db')),baseline,'Original baseline differs from A');
  const inspected=facts(b);assertFacts(inspected.facts,baseline.facts);assertOperationArchiveUnchanged(inspected.tables,baseline.tables);
  const bindings=read(path.join(source,'bindings.json')),preflight=assertPortableGold(gold,bindings,inspected);
  const originalBefore=path.join(prior,'before-restore.db'),beforeSnapshot=facts(originalBefore);
  if(args.target==='source')assert.deepEqual(beforeSnapshot.tables,baseline.tables,'Original source was not frozen before upload');
  else assert.equal(beforeSnapshot.tables.receipts.length,0,'Original fresh target contained receipts');
  const restored=uploads.map(upload=>facts(upload.exportPath));
  for(const snapshot of restored) {assertFacts(snapshot.facts,inspected.facts);assertRestoredOperations(snapshot.tables,inspected.tables);assertPortableGold(gold,bindings,snapshot);}
  assertRepeatedRestore(restored[1].tables,restored[0].tables);
  const retainedFiles=fs.readdirSync(prior).filter(name=>fs.statSync(path.join(prior,name)).isFile()).map(name=>({path:path.join(prior,name),sha256:hash(path.join(prior,name))}));
  save('resume-preflight.json',{...stamp(),prior,target:args.target,bPath:b,bSha256:hash(b),...frozenHashes,originalBefore:{path:originalBefore,sha256:hash(originalBefore)},uploads,preflight,retainedFiles});
  checkpoint='current-export-and-postgresql-baseline';
  const current=await exportDb('resume-before.db');assert.deepEqual(facts(current).tables,restored[1].tables,'Current source no longer equals retained second restore');
  const pgBefore=postgresEvidence();save('postgres-before.json',pgBefore);
  checkpoint='all-api-gold';const evidence=await assertGold(bindings);save('api-gold.json',evidence);
  checkpoint='all-browser-assertions';await verifyBrowser(login,bindings);
  checkpoint='postgresql-proof-and-inspect-only-rejections';const database=await assertDatabase(bindings,evidence.identities);
  assert.deepEqual(database.postgres,pgBefore,'Read-only resume changed PostgreSQL');
  checkpoint='final-portable-state';const after=await exportDb('resume-after.db');assert.deepEqual(facts(after).tables,restored[1].tables,'Read-only resume changed portable state');
  for(const retained of retainedFiles)assert.equal(hash(retained.path),retained.sha256,'Original evidence changed during resume');
  assert.equal(hash(b),args.sha256.toLowerCase(),'Exact B changed during resume');
  assert.deepEqual(assertFrozenInputs(frozenInputs),frozenHashes,'Frozen inputs changed during resume');
  save('restore.json',{...stamp(),target:args.target,bPath:b,bSha256:hash(b),imports:uploads.map(upload=>({status:upload.response.status,body:upload.response.body})),originalUploads:uploads,resumedFrom:prior,readOnlyResume:true,newUploads:0,evidence,database,requests,resumeEvidence:{preflight:path.join(out,'resume-preflight.json'),before:current,after,originalFiles:retainedFiles},pending:['API-only restart phase','edit/revoke/merge/delete-restore and draft refresh','negative-copy and sensitivity phases']});
} else if(phase==='restart-api') {
  assert(args.restore,'restore=successful restore evidence directory required');const restored=read(path.join(path.resolve(args.restore),'restore.json'));assert.equal(restored.runId,ready.RunId);
  const bindings=read(path.join(source,'bindings.json'));await assertGold(bindings);const before=postgresEvidence();
  const requestId=crypto.randomUUID();const requestPath=path.join(runtime,'restart-api.request.json');
  fs.writeFileSync(requestPath,JSON.stringify({requestId}),{flag:'wx'});
  const resultPath=path.join(runtime,`restart-api-${requestId}.result.json`);const deadline=Date.now()+270000;
  while(!fs.existsSync(resultPath)) {assert(Date.now()<deadline,'Aspire restart control timed out');await new Promise(resolve=>setTimeout(resolve,250));}
  const restart=read(resultPath);assert.equal(restart.Success,true,'Aspire API restart failed');assert.equal(restart.RequestId,requestId);assert.notEqual(restart.Before.StartTimeStamp,restart.After.StartTimeStamp,'API process was not replaced');
  // The API has a new process; the task-owned PostgreSQL container, mounts and all durable rows are identical.
  const after=postgresEvidence();assert.deepEqual(after,before,'API restart changed PostgreSQL identity or durable state');await authenticate();const evidence=await assertGold(bindings);await verifyBrowser(login,bindings);
  const database=await assertDatabase(bindings,evidence.identities);save('after-restart.json',{...stamp(),restart,before,after,evidence,database});
} else if(phase==='verify-restart') {
  const bindings=read(path.join(source,'bindings.json'));const evidence=await assertGold(bindings);await verifyBrowser(login,bindings);save('after-restart.json',{...stamp(),evidence,requires:'Retain external API restart event and unchanged PostgreSQL container ID; this phase alone does not establish a restart occurred'});
} else if(phase==='followups') {
  assert(!fs.existsSync(path.join(out,'followups.json')),'Followups already completed');
  const restored=read(path.join(path.resolve(args.restore??out),'restore.json'));assert.equal(restored.runId,ready.RunId);assert.equal(restored.target,'source');assert(restored.evidence&&restored.database,'Finish frozen restore before new entry');
  const bindings=read(path.join(source,'bindings.json'));const created={};
  const preflightPath=path.join(out,'followups-preflight.json');
  if(!fs.existsSync(preflightPath)) {
    assert(!fs.readdirSync(out).some(name=>name.startsWith('followup-')),'Mutation ledger exists without preflight');
    const evidence=await assertGold(bindings);const before=await exportDb('before-followups.db');
    durableSave('followups-preflight.json',{...stamp(),evidence,beforeSha256:hash(before)});
  } else assert.equal(args.resume,'true','Partial followups require explicit resume=true with the same durable ledger');
  const preflight=read(preflightPath);assert.equal(preflight.runId,ready.RunId);assert.equal(hash(path.join(out,'before-followups.db')),preflight.beforeSha256);
  const identities=preflight.evidence.identities;
  for(const f of gold.followups) {const r={...f,items:[f]};const id=await apiReceipt(r,bindings,false,f.key);const trip=await api(`/api/trips?receiptId=${id}`);const item=trip.receipt.items[0];assert.equal(item.normalizationReviewState,f.expectedState);if(f.expectedIdentity)assert.equal(item.normalizedDescriptionId,identities[f.expectedIdentity]);created[f.key]=trip;}
  const retainedFinal=path.join(out,'after-followups.db');
  const exported=await exportDb(fs.existsSync(retainedFinal)?`after-followups-resume-${Date.now()}.db`:'after-followups.db');const final=facts(exported);
  if(exported!==retainedFinal)assert.deepEqual(final.tables,facts(retainedFinal).tables,'Live followups changed since retained final export');
  assert.equal(final.tables.receipts.length,gold.afterFollowups.receipts);assert.equal(final.tables.receipt_items.length,gold.afterFollowups.items);
  assert.equal(final.tables.transactions.reduce((sum,t)=>sum+money(t.amount),0),money(gold.afterFollowups.USD.transactionTotal));
  const original=facts(path.join(out,'before-followups.db'));for(const [table,rows] of Object.entries(original.tables))for(const row of rows)assert(final.tables[table].some(actual=>JSON.stringify(actual)===JSON.stringify(row)),`Original row changed in ${table}`);
  save('followups.json',{...stamp(),created,evidence:await assertGold(bindings,true)});
} else if(phase==='reject-corrupt') {
  assert(args.b&&args.sha256);const b=path.resolve(args.b);assert.equal(hash(b),args.sha256);
  const results=[];
  for(const name of ['truncated','dangling-mapping','mapping-fingerprint','mapping-revision','review-fingerprint','unsupported-evidence']) {
    const before=await exportDb(`before-${name}.db`),pgBefore=postgresEvidence(),corrupt=path.join(out,`${name}-B.db`);
    if(name==='truncated')fs.writeFileSync(corrupt,fs.readFileSync(b).subarray(0,100),{flag:'wx'});
    else execFileSync('python',[path.join(root,'tools/acceptance/offline-normalization/corrupt.py'),b,corrupt,name],{stdio:'pipe'});
    const result=await importDb(corrupt,hash(corrupt));assert.equal(result.status,400,`${name} was not explicitly rejected`);assert(result.body.detail,`${name} requires explicit problem detail`);
    const after=await exportDb(`after-${name}.db`);assert.deepEqual(facts(before).tables,facts(after).tables,`${name} rejection changed portable state`);assert.deepEqual(postgresEvidence(),pgBefore,`${name} rejection changed PostgreSQL`);results.push({case:name,sha256:hash(corrupt),result});
  }
  assert.equal(hash(b),args.sha256);save('corrupt.json',{...stamp(),bSha256:hash(b),results});
} else if(phase==='sensitivity') {
  assert(args.b&&args.sha256);const b=path.resolve(args.b);assert.equal(hash(b),args.sha256);const bindings=read(path.join(source,'bindings.json'));const evidence=await assertGold(bindings);
  const items=Object.fromEntries(Object.values(evidence.trips).flatMap(t=>t.receipt.items).map(i=>[i.id,structuredClone(i)]));
  items[bindings.items['r1-turkey']].normalizedDescriptionId=evidence.identities.beef12;
  assert.throws(()=>assertIdentities(gold,bindings,items),/Split identity|Distinct products merged/,'Semantic oracle accepted turkey-to-beef misassignment');
  const tampered=path.join(out,'factual-tamper-B.db');execFileSync('python',[path.join(root,'tools/acceptance/offline-normalization/corrupt.py'),b,tampered,'factual-tamper'],{stdio:'pipe'});
  assert.throws(()=>assertFacts(facts(tampered).facts,read(path.join(source,'baseline.json')).facts),/Raw factual\/financial baseline changed/,'Baseline accepted financial tampering');
  assert.equal(hash(b),args.sha256);save('sensitivity.json',{...stamp(),semantic:'Detected deliberately wrong turkey-to-beef projected assignment with the same production-used independent oracle',factual:'Detected unit-price tamper in separate SQLite copy using the pre-import factual baseline assertion',bSha256:hash(b),tamperedSha256:hash(tampered)});
} else throw Error('Unknown phase');
console.log(`${phase} completed; private evidence: ${out}. This is a phase result, not overall acceptance.`);
} catch(error) {
  if(phase==='followups')durableSave(`followups-failure-${Date.now()}.json`,{...stamp(),error:{name:error.name,message:error.message,stack:error.stack},requests});
  if(phase==='restore-resume')save('failure-checkpoint.json',{phase,checkpoint,passed:false,error:{name:error.name,message:error.message,stack:error.stack},runtime,prior:args.prior,requests,utc:new Date().toISOString(),newUploads:0});
  throw error;
}
