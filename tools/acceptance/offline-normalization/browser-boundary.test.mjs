import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import {execFileSync} from 'node:child_process';
import {matchesPurchasePreview,assertPurchasePreview,assertBrowserPurchase,assertExactLoginEffects,observeResponse,loadBrowserBoundary} from './browser-boundary.mjs';
import {runWithLifecycleFailureCapture} from './lifecycle-completion.mjs';

const source={location:'store',description:'oats',itemCode:'500'};
function response({method='POST',body=null,throws=false,status=200}={}) {
  return {url:()=> 'https://localhost/api/receipt-items/normalization-preview',status:()=>status,request:()=>({method:()=>method,postDataJSON:()=>{if(throws) throw Error('non-JSON');return body;}})};
}
const preview={sourceFingerprint:'known-source',mappingId:null,mappingRevision:null,targetId:null,targetRevision:null,targetName:null,excluded:false,sourceAmbiguous:false,candidates:[{id:'known-target',revision:'target-revision',name:'oats'}],total:1,offset:0,limit:10};
test('successful transport and full source/target response bind even when request body is absent',()=>{
  for(const options of [{body:null},{throws:true},{body:'not observable'}]) {assert(matchesPurchasePreview(response(options)));assertPurchasePreview(structuredClone(preview),preview);}
  for(const options of [{method:'OPTIONS'},{status:400},{status:201}]) assert.equal(matchesPurchasePreview(response(options)),false);
  for(const field of ['sourceFingerprint','targetId','targetRevision','mappingRevision','offset','limit']) assert.throws(()=>assertPurchasePreview({...preview,[field]:'wrong'},preview));
  assert.throws(()=>assertPurchasePreview({...preview,candidates:[{id:'other',revision:'target-revision',name:'oats'}]},preview));
});
test('browser receipt binds exact known item, source and independently captured PG revision',()=>{
  const item={id:'item',receiptId:'receipt',normalizationRevision:'revision',description:'oats',receiptItemCode:'500',unitPrice:3};
  const future={receiptId:'receipt',item},trip={receipt:{receipt:{id:'receipt',location:'store'},items:[item]}};
  const pg={state:{items:[{Id:'item',ReceiptId:'receipt',NormalizationRevision:'revision',Description:'oats',ReceiptItemCode:'500',UnitPrice:3}]}};
  assertBrowserPurchase(trip,future,pg,source);
  for(const change of [{id:'wrong'},{normalizationRevision:'wrong'},{description:'other'}]) assert.throws(()=>assertBrowserPurchase({...trip,receipt:{...trip.receipt,items:[{...item,...change}]}},future,pg,source));
  assert.throws(()=>assertBrowserPurchase(trip,future,{state:{items:[{...pg.state.items[0],NormalizationRevision:'wrong'}]}},source));
});
test('wait rejection is observed immediately and reaches lifecycle failure capture',async()=>{
  for(const kind of ['predicate','timeout']) {
    const failure=Error(kind);let capture;
    await assert.rejects(runWithLifecycleFailureCapture({run:async()=>{
      const waiting=observeResponse({waitForResponse:predicate=>kind==='predicate'?Promise.resolve().then(()=>predicate(response())):Promise.reject(failure)},()=>{throw failure;});
      await new Promise(resolve=>setImmediate(resolve)); // rejection before its later await
      await waiting();
    },captureFailure:async error=>{capture=error;}}),error=>error===failure);
    assert.equal(capture,failure);
  }
});
const runtime=process.env.BROWSER_BOUNDARY_RUNTIME;
test('real archived completion shape binds restored proof, journals and all portable facts',{skip:!runtime},()=>{
  const prior=path.join(runtime,'lifecycle-completion-18c87514');
  const continuation=JSON.parse(fs.readFileSync(path.join(prior,'continuation.json'),'utf8'));
  const snapshot=file=>JSON.parse(execFileSync('python',[new URL('./snapshot.py',import.meta.url).pathname.replace(/^\/(\w:)/,'$1'),file],{encoding:'utf8',windowsHide:true,maxBuffer:64*1024*1024}));
  const args={runtime,prior,runId:continuation.runId,bSha256:continuation.bSha256,transition:continuation.transition,snapshot};
  const boundary=loadBrowserBoundary(args);
  assert.equal(boundary.expected.tables.receipt_items.length,28);assert.equal(boundary.expected.tables.receipts.length,21);assert(Object.keys(boundary.files).length>200);
  assert.throws(()=>loadBrowserBoundary({...args,bSha256:'wrong'}));
  assert.throws(()=>loadBrowserBoundary({...args,transition:{...continuation.transition,candidateRevision:'wrong'}}));
});

test('actual failed browser retry archive preserves complete portable state and exact login effects',{skip:!runtime},()=>{
  const prior=path.join(runtime,'lifecycle-browser-final-a320674d');
  const continuation=JSON.parse(fs.readFileSync(path.join(prior,'continuation.json'),'utf8'));
  const snapshot=file=>JSON.parse(execFileSync('python',[new URL('./snapshot.py',import.meta.url).pathname.replace(/^\/(\w:)/,'$1'),file],{encoding:'utf8',windowsHide:true,maxBuffer:64*1024*1024}));
  const args={runtime,prior,runId:continuation.runId,bSha256:continuation.bSha256,transition:continuation.transition,snapshot};
  const boundary=loadBrowserBoundary(args);assert.equal(boundary.expected.tables.receipt_items.length,28);assert.equal(Object.keys(boundary.allTables.tables).length,42);assert.equal(boundary.rawPreviewBodyAvailable,false);
  assert.throws(()=>loadBrowserBoundary({...args,runId:'wrong'}));
});

test('archived login bridge rejects any unrelated table, user, audit or timestamp change',{skip:!runtime},()=>{
  const read=(dir,name)=>JSON.parse(fs.readFileSync(path.join(runtime,dir,name),'utf8').replace(/^\uFEFF/,''));
  const original=read('lifecycle-completion-18c87514','safe-stop-all-tables.json');
  const dir='lifecycle-browser-final-a320674d',current=read(dir,'safe-stop-all-tables.assessed.json'),req=read(dir,'authentication-request.json'),res=read(dir,'authentication-response.json');
  assertExactLoginEffects(original,current,req,res);
  for(const mutate of [v=>{v.tables['library.Accounts'][0].Name='unexpected';},v=>{v.tables['identity.AspNetUsers'][0].Email='unexpected';},v=>{v.tables['audit.AuthAuditLogs'].find(x=>!original.tables['audit.AuthAuditLogs'].some(y=>y.Id===x.Id)).Success=false;},v=>{v.tables['identity.AspNetUsers'][0].LastLoginAt='2026-10-24T18:11:57.283033+00:00';}]) {const changed=structuredClone(current);mutate(changed);assert.throws(()=>assertExactLoginEffects(original,changed,req,res),mutate.toString());}
});
