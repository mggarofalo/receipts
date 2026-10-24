import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import {execFileSync} from 'node:child_process';
import {matchesPurchasePreview,observeResponse,loadBrowserBoundary} from './browser-boundary.mjs';
import {runWithLifecycleFailureCapture} from './lifecycle-completion.mjs';

const source={location:'store',description:'oats',itemCode:'500'};
function response({method='POST',body={source,offset:0},throws=false,ok=true}={}) {
  return {url:()=> 'https://localhost/api/receipt-items/normalization-preview',ok:()=>ok,request:()=>({method:()=>method,postDataJSON:()=>{if(throws) throw Error('non-JSON');return body;}})};
}
test('preview matches only successful exact POST source with an object body',()=>{
  assert(matchesPurchasePreview(response(),source));
  for(const options of [{method:'OPTIONS',body:null},{body:null},{body:[]},{body:'plain'},{throws:true},{body:{source:null,offset:0}},{body:{source:{...source,location:'other'},offset:0}},{body:{source,offset:20}},{body:{source,offset:0,query:'search'}},{ok:false}]) assert.equal(matchesPurchasePreview(response(options),source),false);
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
