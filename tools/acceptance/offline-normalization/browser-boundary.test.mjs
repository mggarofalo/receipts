import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import {execFileSync} from 'node:child_process';
import {matchesPurchasePreview,assertPurchasePreview,assertBrowserPurchase,assertExactLoginEffects,observeResponse,loadBrowserBoundary,browserRequestFailure,matchesCandidateLookup,assertBeforeWriteJournal,settleCandidateLookup,assertBrowserPreviewSource,assertBeforeWriteAssessment} from './browser-boundary.mjs';
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

// Failed lookups remain failures, including aborts: the scenario settles retrieval first.
test('candidate readiness binds the exact source and successful transport',()=>{
  const url='https://localhost/api/receipt-items/normalization-candidates?'+new URLSearchParams(source);
  const lookup=(overrides={})=>({url:()=>url,status:()=>200,request:()=>({method:()=> 'GET'}),...overrides});
  assert(matchesCandidateLookup(lookup(),source));
  for(const changed of [lookup({status:()=>503}),lookup({request:()=>({method:()=> 'POST'})}),lookup({url:()=>url.replace('oats','rice')}),lookup({url:()=>url.replace('location=store','location=other')}),lookup({url:()=>url.replace('itemCode=500','itemCode=999')}),lookup({url:()=>url.replace('normalization-candidates','other')})]) assert.equal(matchesCandidateLookup(changed,source),false);
});
test('transport diagnostics retain exact failure, source query, timing and lifecycle without exempting aborts',()=>{
  for(const errorText of ['net::ERR_ABORTED','net::ERR_CONNECTION_RESET',null]) {
    const req={url:()=> 'https://localhost/api/receipt-items/normalization-candidates?description=oats',method:()=> 'GET',failure:()=>errorText?{errorText}:null,redirectedFrom:()=>null,timing:()=>({startTime:123})};
    const entry=browserRequestFailure(req,'review-later-click');
    assert.equal(entry.transportFailed,true);assert.equal(entry.errorText,errorText);assert.equal(entry.query,'?description=oats');assert.equal(entry.lifecycle,'review-later-click');assert.equal(entry.timing.startTime,123);assert(entry.utc);assert.equal(entry.requestChain.length,1);
  }
});

test('assessed before-write stop accepts same safe boundary and rejects mutation or unrelated state',{skip:!runtime || !fs.existsSync(path.join(runtime,'lifecycle-browser-completion-bafc022e/browser-before-write-assessment.json'))},()=>{
  const prior=path.join(runtime,'lifecycle-browser-completion-bafc022e');
  const continuation=JSON.parse(fs.readFileSync(path.join(prior,'continuation.json'),'utf8'));
  const snapshot=file=>JSON.parse(execFileSync('python',[new URL('./snapshot.py',import.meta.url).pathname.replace(/^\/(\w:)/,'$1'),file],{encoding:'utf8',windowsHide:true,maxBuffer:64*1024*1024}));
  const args={runtime,prior,runId:continuation.runId,bSha256:continuation.bSha256,transition:continuation.transition,snapshot};
  const boundary=loadBrowserBoundary(args);assert.equal(Object.keys(boundary.allTables.tables).length,42);assert.equal(boundary.future.item.unitPrice,3);
  assert.throws(()=>loadBrowserBoundary({...args,bSha256:'wrong'}));
  assert.throws(()=>loadBrowserBoundary({...args,snapshot:file=>{const actual=snapshot(file);if(file.endsWith('safe-stop.db')) actual.tables.receipt_items[0].unit_price=99;return actual;}}));
});

test('before-write journal refuses saves, competing edits, missing responses and unsuccessful preview',()=>{
  const item={id:'item',unitPrice:3};
  const requests=[{url:'/api/normalization-ai/connection',method:'GET'},{url:'/api/receipt-items/item',method:'GET'},{url:'/api/receipt-items/normalization-preview',method:'POST'},{url:'/api/receipt-items/item',method:'GET'}];
  const responses=requests.map(req=>({...req,status:200,text:JSON.stringify(item)}));
  assertBeforeWriteJournal(requests,responses,item);
  for(const mutate of [v=>{v.requests[3].method='PUT';},v=>{v.requests.push({url:'/api/receipt-items/item',method:'PUT'});},v=>{v.responses.pop();},v=>{v.responses[2].status=503;},v=>{v.responses[3].text=JSON.stringify({...item,unitPrice:4});}]) {
    const changed=structuredClone({requests,responses});mutate(changed);assert.throws(()=>assertBeforeWriteJournal(changed.requests,changed.responses,item));
  }
});

test('shortlist settlement rejects canceled, failed or malformed completed bodies',async()=>{
  assert.deepEqual(await settleCandidateLookup({finished:async()=>null,json:async()=>[]}),[]);
  for(const response of [{finished:async()=> 'net::ERR_ABORTED',json:async()=>[]},{finished:async()=>{throw Error('network failure');},json:async()=>[]},{finished:async()=>null,json:async()=>({message:'unavailable'})}]) await assert.rejects(settleCandidateLookup(response));
});
test('assessed recovery refuses claimed writes or retries and binds preview to actual purchase source',()=>{
  const assessment={kind:'assessed-browser-before-write-20261024',status:'failed-safe-stop',runnerExit:1,failedGate:'browser-cache-refresh-and-unsaved-draft',captureWasReadOnly:true,browserSavePlanned:false,competingPriceWritePerformed:false,retryPerformed:false};
  assertBeforeWriteAssessment(assessment);
  for(const key of ['browserSavePlanned','competingPriceWritePerformed','retryPerformed']) assert.throws(()=>assertBeforeWriteAssessment({...assessment,[key]:true}));
  const item={receiptId:'receipt',description:'oats',receiptItemCode:'500'},pg={state:{receipts:[{Id:'receipt',Location:'store'}]}};
  assertBrowserPreviewSource(source,item,pg);
  for(const key of ['location','description','itemCode']) assert.throws(()=>assertBrowserPreviewSource({...source,[key]:'other'},item,pg));
});
