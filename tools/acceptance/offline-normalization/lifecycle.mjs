import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import assert from 'node:assert/strict';
import { fileURLToPath } from 'node:url';
import { createRequire } from 'node:module';
import { execFileSync } from 'node:child_process';
import { assertLifecycleResume, assertSettled, assertRevisionTransition, assertUpgradePreserved, assertAuthBoundaryRecovery, runAuthenticationBoundary } from './lifecycle-resume.mjs';

import { assertCompletionBoundary, assertIndependentProof, assertRestoreDecision, assertCompletionFacts, runWithLifecycleFailureCapture, journalBrowserSave, redactedFailure, bindCompletionEvidence } from './lifecycle-completion.mjs';

import {matchesPurchasePreview,observeResponse,loadBrowserBoundary} from './browser-boundary.mjs';

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
  let resume, completion, browserOnly;
  assert([args.prior,args.continuationPrior,args.browserPrior].filter(Boolean).length<=1, 'Choose one assessed boundary');
  const transition=args.transition?read(path.resolve(args.transition)):null;
  const launchSource=read(path.join(runtime,'source.json'));
  if(args.browserPrior) {
    assert(transition && !args.authRecovery,'Browser completion requires the retained transition without auth exceptions');
    browserOnly=loadBrowserBoundary({prior:args.browserPrior,runtime,runId:ready.RunId,bSha256:records.sourceRestore.bSha256,snapshot,transition});
    args.continuationPrior=browserOnly.continuation.prior;
  }
  if(args.prior) {
    assert(transition,'Resume requires transition=PRIVATE_REVISION_RECORD');
    assert.equal(launchSource.RunId,ready.RunId);
    const git=argv=>execFileSync('git',argv,{cwd:root,encoding:'utf8',windowsHide:true}).trim();
    const head=git(['rev-parse','HEAD']);
    assert.match(transition.candidateRevision,/^[a-f0-9]{40}$/i);
    const revisions=assertRevisionTransition(transition,launchSource,head,git(['status','--porcelain','--untracked-files=normal']),git(['diff','--name-only','--no-renames',transition.candidateRevision,head]).split(/\r?\n/).filter(Boolean));
    const prior=path.resolve(args.prior); assert(inside(runtime,prior) && prior!==runtime);
    const files=['failure.json','baseline.json','before-stale-item.db',...Array.from({length:3},(_,i)=>`created-${i+1}.json`),...Array.from({length:26},(_,i)=>`request-${i+2}.json`),...Array.from({length:26},(_,i)=>`response-${i+2}.json`)];
    assert(!fs.existsSync(path.join(prior,'lifecycle.json')),'Prior lifecycle already passed');
    assert(!fs.readdirSync(prior).some(n=>/^request-\d+\.json$/.test(n) && Number(n.match(/\d+/)[0])>27),'Unsupported later mutation journal');
    const bundle={failure:read(path.join(prior,'failure.json')),baseline:read(path.join(prior,'baseline.json')),created:Array.from({length:3},(_,i)=>read(path.join(prior,`created-${i+1}.json`))),requests:Array.from({length:26},(_,i)=>read(path.join(prior,`request-${i+2}.json`))),responses:Array.from({length:26},(_,i)=>read(path.join(prior,`response-${i+2}.json`)))};
    resume={...assertLifecycleResume(bundle,ready.RunId,records.sourceRestore.bSha256),revisions,prior,baseline:bundle.baseline,expected:snapshot(path.join(prior,'before-stale-item.db')),files:Object.fromEntries(files.map(name=>[name,hash(path.join(prior,name))]))};
    assert.equal(resume.baseline.followupHash,followupHash);
    assert.deepEqual(resume.baseline.snapshot.tables,baseline.tables);
    resume.transitionFiles={[path.resolve(args.transition)]:hash(path.resolve(args.transition)),[path.join(runtime,'source.json')]:hash(path.join(runtime,'source.json'))};
    for(const key of ['beforePostgres','beforeMigrationHistory']) {
      const retained=path.resolve(transition[key+'Path']);
      assert(inside(path.dirname(runtime),retained),'Transition evidence must stay inside the private runtime parent');
      assert.equal(hash(retained),transition[key+'Sha256'],'Transition evidence hash mismatch');
      resume.transitionFiles[retained]=transition[key+'Sha256'];
      resume[key]=read(retained);
    }
    if(args.authRecovery) {
      const recordPath=path.resolve(args.authRecovery);
      assert(inside(runtime,recordPath),'Auth recovery record must remain private in this runtime');
      const record=read(recordPath);resume.transitionFiles[recordPath]=hash(recordPath);
      const failed=path.join(runtime,record.failedAttempt);
      assert.equal(path.basename(failed),'lifecycle-integrated-636a4e0f');assert.equal(path.dirname(failed),runtime);
      const load=(entry,expectedParent)=>{
        const file=path.resolve(entry.path);assert.equal(path.dirname(file),expectedParent);
        assert.equal(hash(file),entry.sha256,'Private auth recovery evidence hash mismatch');
        resume.transitionFiles[file]=entry.sha256;return file;
      };
      assert.equal(load(record.evidence.originalBefore,path.dirname(path.resolve(transition.beforePostgresPath))),path.resolve(transition.beforePostgresPath));
      assert.equal(record.evidence.originalBefore.sha256,transition.beforePostgresSha256);
      assert.equal(load(record.evidence.transition,path.dirname(path.resolve(args.transition))),path.resolve(args.transition));
      const summaryPath=load(record.evidence.summary,failed),auditsPath=load(record.evidence.addedAudits,failed);
      assert.equal(path.basename(summaryPath),'safe-stop-summary.json');assert.equal(path.basename(auditsPath),'added-audit-private.json');
      const summary=read(summaryPath);assert.equal(summary.candidateRevision,transition.candidateRevision);
      assert.equal(record.evidence.settled.length,2);
      const samples=record.evidence.settled.map(entry=>{
        const file=load(entry,failed);assert.match(path.basename(file),/^initial-settlement-\d+\.json$/);return read(file);
      });
      assert.notEqual(record.evidence.settled[0].path,record.evidence.settled[1].path);assert.deepEqual(samples[0],samples[1]);
      const log=load(record.evidence.log,runtime);assert.equal(path.basename(log),record.failedAttempt+'.log');
      assert.equal(hash(log),summary.logSha256);assert(fs.readFileSync(log,'utf8').includes(summary.failedAssertion));
      assert(fs.readFileSync(log,'utf8').includes(record.auditId));assert(fs.readFileSync(log,'utf8').includes(record.userId));
      const logStat=fs.statSync(log);
      assert.equal(record.failedRunStartedUtc,logStat.birthtime.toISOString());assert.equal(record.failedRunStoppedUtc,logStat.mtime.toISOString());
      resume.authRecovery={record,summary,addedAudits:read(auditsPath),retainedAfter:samples[0]};
    }
  }
  if(args.continuationPrior) {
    assert(transition && !args.authRecovery,'Completion requires a fresh transition without auth exceptions');
    assert.equal(launchSource.RunId,ready.RunId);
    const prior=path.resolve(args.continuationPrior);
    assert.equal(path.dirname(prior),runtime);assert.equal(path.basename(prior),'lifecycle-final-196dbf68');
    assert(!fs.existsSync(path.join(prior,'lifecycle.json')));
    const files=Object.fromEntries(fs.readdirSync(prior).map(name=>{
      assert(fs.statSync(path.join(prior,name)).isFile(),'Unexpected nested prior evidence');return [name,hash(path.join(prior,name))];
    }));
    for(const prefix of ['request','response']) assert.deepEqual(Object.keys(files).filter(n=>n.startsWith(prefix+'-')).sort(),Array.from({length:48},(_,i)=>`${prefix}-${i+2}.json`).sort());
    const load=name=>read(path.join(prior,name));
    const bundle={failure:load('failure.json'),summary:load('safe-stop-summary.json'),arrangement:load('resumed-existing-arrangement.json'),revoke:load('3-revoke-history-and-future-reuse.json'),merge:load('4-intentional-same-product-merge.json'),stop:load('safe-stop-postgres.json'),requests:Array.from({length:48},(_,i)=>load(`request-${i+2}.json`)),responses:Array.from({length:48},(_,i)=>load(`response-${i+2}.json`))};
    const git=argv=>execFileSync('git',argv,{cwd:root,encoding:'utf8',windowsHide:true}).trim();
    const head=git(['rev-parse','HEAD']);
    const revisions=assertRevisionTransition(transition,launchSource,head,git(['status','--porcelain','--untracked-files=normal']),git(['diff','--name-only','--no-renames',transition.candidateRevision,head]).split(/\r?\n/).filter(Boolean));
    assert.deepEqual(transition.allowedAddedMigrations,[],'This repair requires no migrations');
    assert.equal(hash(path.join(prior,'safe-stop-database.sql')),bundle.summary.databaseDumpSha256);
    const retainedResume=load('resume.json');
    const retainedEvidenceFiles=bindCompletionEvidence({resume:retainedResume,runtime,transitionPath:path.resolve(args.transition)});
    completion={...assertCompletionBoundary(bundle,ready.RunId,records.sourceRestore.bSha256),prior,files,revisions,stop:bundle.stop,retainedEvidenceFiles};
    const retainedBaseline=load('baseline.json');
    assert.equal(retainedBaseline.followupHash,followupHash);assert.deepEqual(retainedBaseline.snapshot.tables,baseline.tables);
    completion.originalPg=retainedBaseline.preservationPostgres;
    completion.transitionFiles={[path.resolve(args.transition)]:hash(path.resolve(args.transition)),[path.join(runtime,'source.json')]:hash(path.join(runtime,'source.json'))};
    for(const key of ['beforePostgres','beforeMigrationHistory']) {
      const file=path.resolve(transition[key+'Path']);assert(inside(path.dirname(runtime),file));
      assert.equal(hash(file),transition[key+'Sha256']);completion.transitionFiles[file]=transition[key+'Sha256'];completion[key]=read(file);
    }
    assert.deepEqual(completion.beforePostgres,completion.stop,'Fresh upgrade must start at the full retained safe stop');
  }
  assert(!args.authRecovery || resume,'Auth recovery requires the assessed lifecycle resume');
  if(browserOnly) {
    const priorBaseline=read(path.join(browserOnly.prior,'baseline.json'));
    assert.equal(priorBaseline.followupHash,followupHash);assert.deepEqual(priorBaseline.snapshot.tables,baseline.tables);
    completion.beforePostgres=browserOnly.stop;
  }
  if(args.preflightOnly==='true') {
    assert(completion,'Read-only preflight requires the assessed completion boundary');
    console.log(JSON.stringify({status:browserOnly?'browser-preflight-passed':'completion-preflight-passed',runId:ready.RunId,boundFiles:Object.keys(completion.retainedEvidenceFiles).length,newReceiptCreations:0}));
    return;
  }
  fs.mkdirSync(out);
  const save = (name,value) => fs.writeFileSync(path.join(out,name), JSON.stringify(value,null,2), {flag:'wx'});
  const stamp = () => ({runId:ready.RunId,utc:new Date().toISOString(),phase:'lifecycle',bSha256:records.sourceRestore.bSha256});
  let token;
  let lastRequest=0,requestNumber=0;
  const pause=ms=>new Promise(resolve=>setTimeout(resolve,ms));
  const journal=(name,value)=>{const fd=fs.openSync(path.join(out,name),'wx');try{fs.writeFileSync(fd,JSON.stringify(value,null,2));fs.fsyncSync(fd);}finally{fs.closeSync(fd);}};
  const completed=browserOnly?[...browserOnly.completed]:completion?[]:resume?['provider-unavailable-manual-entry']:[];
  const created=completion?[...completion.created]:resume?[...resume.created]:[];
  const sensitive=[];let capturePostgres,captureExport;
  const safeError=error=>redactedFailure(error,sensitive);
  await runWithLifecycleFailureCapture({run:async()=>{
  capturePostgres=postgres;captureExport=exported;
  async function request(url, body, method = body === undefined ? 'GET' : 'POST', status) {
    await pause(Math.max(0,lastRequest+850-Date.now()));lastRequest=Date.now();
    const number=++requestNumber;
    const authentication=url==='/api/auth/login';
    if(authentication) journal('authentication-request.json',{utc:new Date().toISOString(),url,method,credentialsRedacted:true});
    else if(token)journal(`request-${number}.json`,{utc:new Date().toISOString(),url,method,body});
    const response = await fetch(new URL(url,ready.Api), {method,headers:{...(token ? {Authorization:`Bearer ${token}`} : {}),...(body === undefined ? {} : {'Content-Type':'application/json'})},body:body === undefined ? undefined : JSON.stringify(body),signal:AbortSignal.timeout(60000)});
    const text = await response.text();
    if(authentication) journal('authentication-response.json',{utc:new Date().toISOString(),url,method,status:response.status,bodyRedacted:true});
    else if(token)journal(`response-${number}.json`,{utc:new Date().toISOString(),url,method,status:response.status,text});
    let value;try {value=text?JSON.parse(text):null;} catch(error) {if(authentication) throw Error('Authentication response was not valid JSON; body redacted');throw error;}
    if(authentication) sensitive.push(value?.accessToken,value?.refreshToken);
    if (status !== undefined) { assert.equal(response.status,status, `${method} ${url}`); assert(value?.detail, 'Reasoned rejection must have RFC 9457 detail'); }
    else assert(response.ok, `${method} ${url}: ${response.status}; inspect private evidence/runtime log`);
    return value;
  }
  let secrets,password;
  try {secrets=read(path.join(runtime,'secrets.json'));password=read(path.join(runtime,'acceptance-password.json')).password;sensitive.push(password);}
  catch {throw Error('Private authentication configuration unavailable; content redacted');}
  async function exported(name) {
    await pause(Math.max(0,lastRequest+850-Date.now()));lastRequest=Date.now();
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
  function migrationHistory() {
    const query='SELECT coalesce(json_agg(x ORDER BY x."MigrationId"),\'[]\') FROM public."__EFMigrationsHistory" x;';
    return JSON.parse(execFileSync('docker',['exec',ready.RunId+'-postgres','sh','-c','PGPASSWORD="$POSTGRES_PASSWORD" exec psql "$@"','sh','-U','postgres','-d','receiptsdb','-X','-q','-A','-t','-v','ON_ERROR_STOP=1','-c',query],{encoding:'utf8',windowsHide:true}));
  }
  // Settle projections before taking strict, complete PostgreSQL atomicity baselines.
  // No revision, audit, proof or business field is removed from any comparison.
  async function settled(name) {
    const deadline=Date.now()+180000; let previous;
    while(Date.now()<deadline) {
      const pg=postgres();
      journal(`${name}-${Date.now()}.json`,pg);
      let readyToMeasure=false;try{assertSettled(pg);readyToMeasure=true;}catch{}
      if(readyToMeasure && previous && JSON.stringify(pg)===JSON.stringify(previous)) return pg;
      previous=pg; await pause(5000);
    }
    throw Error('PostgreSQL projections did not settle; no mutation attempted at this gate');
  }
  const {login,postgres:currentPg}=await runAuthenticationBoundary({
    verifyUpgrade:async()=>{
      const preAuthPg=await settled('initial-settlement');
      if(completion) {
        assert.equal(transition.postgresContainerId,preAuthPg.container.Id);
        const migrations=migrationHistory();
        assertUpgradePreserved(completion.beforePostgres,preAuthPg,completion.beforeMigrationHistory,migrations,[]);
        if(browserOnly) {
          for(const [file,sha] of Object.entries(browserOnly.files)) assert.equal(hash(file),sha,'Prior browser evidence changed before authentication');
          assert.deepEqual(captureAllTables(preAuthPg.container),browserOnly.allTables,'Complete current PostgreSQL differs from the retained browser stop');
        }
        journal('upgrade-preserved.json',{...stamp(),postgres:preAuthPg,migrations,...completion.revisions,strictComparisonBeforeAuthentication:true});
      }
      if(resume) {
        assert.equal(transition.postgresContainerId,preAuthPg.container.Id);
        const migrations=migrationHistory();
        const expected=resume.authRecovery?assertAuthBoundaryRecovery({...resume.authRecovery,before:resume.beforePostgres,after:preAuthPg,runId:ready.RunId,applicationRevision:resume.revisions.applicationRevision,beforePostgresSha256:transition.beforePostgresSha256}):resume.beforePostgres;
        assertUpgradePreserved(expected,preAuthPg,resume.beforeMigrationHistory,migrations,transition.allowedAddedMigrations);
        journal('upgrade-preserved.json',{...stamp(),postgres:preAuthPg,migrations,...resume.revisions,authRecovery:resume.authRecovery?.record,strictComparisonBeforeAuthentication:true});
      }
    },
    authenticate:async()=>{
      await pause(65000);
      const value=await request('/api/auth/login',{email:secrets.AdminEmail,password});
      assert.equal(value.mustResetPassword,false);token=value.accessToken;return value;
    },
    captureBaseline:()=>settled('authenticated-lifecycle-settlement')
  });
  const originalPg=completion?.originalPg ?? resume?.baseline.postgres ?? currentPg;
  assert.equal(currentPg.container.Id,originalPg.container.Id); assert.deepEqual(currentPg.container.Mounts,originalPg.container.Mounts);
  assert.equal(currentPg.state.items.filter(i => !i.DeletedAt).length,completion?28:resume?25:22);
  assert.equal(currentPg.state.receipts.filter(i => !i.DeletedAt).length,completion?21:resume?18:15);
  const beforePortable=await exported('before-lifecycle.db');
  if(completion) assertPreservedRows(baseline.tables,beforePortable.tables);
  else assert.deepEqual(beforePortable.tables,resume?.expected.tables ?? baseline.tables,'Current installation differs from retained lifecycle boundary');
  if(browserOnly) assert.deepEqual(beforePortable.tables,browserOnly.expected.tables,'Current export differs from completed restore');
  save('baseline.json',{...stamp(),followupHash,prerequisites:records,postgres:currentPg,preservationPostgres:originalPg,snapshot:baseline,authenticationCompleted:true});
  if(resume) save('resume.json',{...stamp(),...resume.revisions,prior:resume.prior,files:resume.files,syntheticReceiptIds:resume.created,priorFailureRetained:true,newCreationsBeforeStaleGate:0,originalFailurePassed:false,launchSource:read(path.join(runtime,'source.json')),transition,transitionSha256:hash(path.resolve(args.transition)),authRecovery:resume.authRecovery?.record});
  if(completion) journal('continuation.json',{...stamp(),...completion.revisions,prior:completion.prior,files:completion.files,transition,transitionFiles:completion.transitionFiles,retainedEvidenceFiles:completion.retainedEvidenceFiles,originalFailurePassed:false,newReceiptCreations:0});
  let step = 0;
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
  const tag = completion?.tag ?? resume?.tag ?? 'Lifecycle '+uuid(); const location = completion?.location ?? resume?.location ?? tag+' store';
  const line = completion?.line ?? resume?.line ?? {description:tag+' RAW OATS 500G',receiptItemCode:'LIFE-500',quantity:2,unitPrice:3,category:'Groceries',subcategory:tag};
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
    const availability=await request('/api/normalization-ai/connection'); assert.equal(availability.configured,false,'Use the provider-disabled acceptance runtime');
    if(completion) {
      if(browserOnly) {await finishBrowserBoundary();return;}
      await finishRestoreBoundary();
      return;
    }
    let manual,mapped,p,independent,financial;
    if(!resume) {
    const category=rows(await request('/api/categories?limit=500')).find(c=>c.name===line.category && c.isActive); assert(category);
    await request('/api/subcategories',{name:line.subcategory,categoryId:category.id,isActive:true});
    manual=await create(line,await intentFor(line,'confirmNew',{newName:tag+' Oats 500 g',rememberSource:true}));
    assert.equal(manual.item.normalizationReviewState,'verified'); await finances(manual.receiptId);
    await checkpoint('provider-unavailable-manual-entry',{availability,manual});
    mapped=await create(line); assert.equal(mapped.item.normalizationReviewState,'verified'); assert.equal(mapped.item.normalizationReviewReason,'approved_mapping');
    p=await preview(location,line); assert(p.mappingId && p.mappingRevision && p.targetRevision);
    independent=await create(line,await intentFor(line,'confirmExisting',{targetId:p.targetId,expectedTargetRevision:p.targetRevision,rememberSource:false}));
    assert.equal(independent.item.normalizationReviewReason,'purchase_review');
    // A financial edit changes the revision, but neither product nor authority.
    await update(mapped.item,{quantity:3,unitPrice:2}); financial=await getItem(mapped.item.id);
    assert.equal(financial.normalizationReviewState,'verified'); assert.equal(financial.normalizedDescriptionId,mapped.item.normalizedDescriptionId); await finances(mapped.receiptId);
    } else {
      ({manual,mapped,p,independent,financial}=resume);
      for(const expected of [manual.item,independent.item,financial]) assert.deepEqual(await getItem(expected.id),expected,'Retained purchase changed before resume');
      await finances(mapped.receiptId);
      save('resumed-existing-arrangement.json',{...stamp(),manual,mapped,independent,financial,availability});
    }
    const staleIntent=resume?.staleRequest.body.normalizationIntent ?? await intentFor(financial,'defer',{expectedItemRevision:mapped.item.normalizationRevision});
    const rejectBefore=await settled('stale-item-settlement'); const portableBefore=await exported('before-stale-item.db');
    journal('before-stale-item-postgres.json',rejectBefore);
    const rejected=await request('/api/receipt-items/'+financial.id,{...line,id:financial.id,description:line.description,unitPrice:91,normalizationIntent:staleIntent},'PUT',409);
    const rejectAfter=postgres();journal('after-stale-item-postgres.json',rejectAfter);
    const portableAfter=await exported('after-stale-item.db');
    assert.deepEqual(rejectAfter,rejectBefore,'Rejected stale edit changed durable facts/proof/audits'); assert.deepEqual(portableAfter.tables,portableBefore.tables);
    await checkpoint('stale-revision-atomic-rejection',{rejected,financial});
    // Parent source scope is part of a mapped purchase's reviewed fingerprint.
    await request('/api/receipts/'+mapped.receiptId,{id:mapped.receiptId,location:tag+' moved store',date:'2026-10-20',taxAmount:0},'PUT');
    const invalid=await getItem(mapped.item.id); assertInvalidated(financial,invalid); await finances(mapped.receiptId);
    const pgInvalid=postgres().state.items.find(i=>i.Id===invalid.id); const changedPreview=await preview(tag+' moved store',invalid);
    assert.equal(pgInvalid.NormalizationSourceHash,changedPreview.sourceFingerprint); assert.notEqual(pgInvalid.ReviewedSourceHash,pgInvalid.NormalizationSourceHash);
    assert(postgres().state.reviewWork.some(w=>w.ItemId===invalid.id),'Invalidated purchase lacks durable review work');
    await checkpoint('receipt-source-edit-invalidates-authority',{financial,invalid,receipt:await finances(mapped.receiptId)});
    const reused=await create(line); assert.equal(reused.item.normalizationReviewState,'verified');
    const revokeBefore=await settled('stale-revoke-settlement');
    const staleRevoke=await request('/api/normalization-catalog/mappings/'+p.mappingId+'/revoke',{expectedRevision:uuid()},'POST',409);
    const revokeAfter=postgres();journal('after-stale-revoke-postgres.json',revokeAfter);
    assert.deepEqual(revokeAfter,revokeBefore,'Rejected mapping revision changed durable state');
    const revoked=await request('/api/normalization-catalog/mappings/'+p.mappingId+'/revoke',{expectedRevision:p.mappingRevision}); assert.equal(revoked.state,'revoked');
    const historical=await getItem(reused.item.id), reviewed=await getItem(independent.item.id);
    assertRevokedHistory(reused.item,historical); assertRevokedHistory(independent.item,reviewed,true);
    const future=await create(line); assert.equal(future.item.normalizationReviewState,'needsReview'); assert.equal(future.item.normalizedDescriptionId,null);
    const staleKnown={kind:'knownSource',clientRowId:uuid(),sourceFingerprint:p.sourceFingerprint,expectedMappingRevision:p.mappingRevision};
    const beforeKnown=await settled('stale-mapping-settlement'); const beforeKnownPortable=await exported('before-stale-mapping.db');
    const knownRejected=await request('/api/receipts/complete',{clientRequestId:uuid(),receipt:{location,date:'2026-10-20',taxAmount:0},items:[{...line,normalizationIntent:staleKnown}],transactions:[{cardId:bindings.card,amount:6,date:'2026-10-20'}],adjustments:[]},'POST',409);
    const afterKnown=postgres();journal('after-stale-mapping-postgres.json',afterKnown);
    const afterKnownPortable=await exported('after-stale-mapping.db');
    assert.deepEqual(afterKnown,beforeKnown); assert.deepEqual(afterKnownPortable.tables,beforeKnownPortable.tables);
    await checkpoint('revoke-history-and-future-reuse',{staleRevoke,revoked,historical,reviewed,future,knownRejected});
    // Two deliberately separate declarations describe the same isolated synthetic product.
    const variant={...line,description:tag+' OATS ALT LABEL',receiptItemCode:'LIFE-ALT'};
    const other=await create(variant,await intentFor(variant,'confirmNew',{newName:tag+' Alternate Oats 500 g',rememberSource:false}));
    assert.notEqual(other.item.normalizedDescriptionId,manual.item.normalizedDescriptionId);
    const merge=await request('/api/normalized-descriptions/'+manual.item.normalizedDescriptionId+'/merge',{discardId:other.item.normalizedDescriptionId});
    const merged=await getItem(other.item.id); assert.equal(merged.normalizedDescriptionId,manual.item.normalizedDescriptionId);
    assert.equal(merged.normalizationReviewState,'verified','Deliberate merge lost current independent purchase review');
    assert.equal(merged.description,variant.description); await finances(other.receiptId);
    const mergePg=await settled('merge-settlement'); assert(!mergePg.state.identities.some(i=>i.Id===other.item.normalizedDescriptionId));
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
    if(resume) {
      for(const [name,sha] of Object.entries(resume.files)) assert.equal(hash(path.join(resume.prior,name)),sha,'Prior evidence changed');
      for(const [file,sha] of Object.entries(resume.transitionFiles)) assert.equal(hash(file),sha,'Transition evidence changed');
    }
    save('lifecycle.json',{...stamp(),completed,syntheticReceiptIds:created,baselineFollowupHash:followupHash,status:'passed'});
  async function finishRestoreBoundary() {
    const {independent,future,failedItem}=completion;
    assert.deepEqual(await getItem(independent.item.id),failedItem,'Failed purchase changed after the retained boundary');
    assert.deepEqual(await getItem(future.item.id),future.item,'Existing browser fixture changed');
    const fresh=await request('/api/receipt-items/normalization-preview',{source:source(location,failedItem),query:independent.item.normalizedDescriptionName,offset:0,limit:20});
    const target=fresh.candidates.find(c=>c.id===independent.item.normalizedDescriptionId);assert(target?.revision,'Current preview lacks the known synthetic target');
    const beforeReview=await settled('before-explicit-reconfirmation');journal('before-reconfirmation-postgres.json',beforeReview);
    await update(failedItem,{normalizationIntent:{kind:'confirmExisting',clientRowId:uuid(),sourceFingerprint:fresh.sourceFingerprint,expectedItemRevision:failedItem.normalizationRevision,targetId:target.id,expectedTargetRevision:target.revision,rememberSource:false}});
    const reviewed=await getItem(failedItem.id);const reviewedPg=postgres();journal('after-reconfirmation-postgres.json',reviewedPg);
    assertRevokedHistory(independent.item,reviewed,true);assert.equal(reviewed.normalizationReviewReason,'purchase_review');
    assert.notEqual(reviewed.normalizationReviewDecisionId,independent.item.normalizationReviewDecisionId,'Reconfirmation must be a fresh decision');
    const proof=assertIndependentProof(reviewedPg,reviewed.id);
    assertCompletionFacts(beforePortable.facts,(await exported('after-reconfirmation.db')).facts);
    await checkpoint('explicit-synthetic-purchase-reconfirmation',{before:failedItem,reviewed,fresh,target});
    const deleteBefore=await trip(independent.receiptId);const reportBefore=await request('/api/reports/spending-by-normalized-description?pageSize=100');
    journal('before-repeat-delete-postgres.json',postgres());
    await request('/api/receipts',[independent.receiptId],'DELETE');
    const deletedPg=postgres();journal('after-repeat-delete-postgres.json',deletedPg);
    assert(deletedPg.state.items.find(i=>i.Id===reviewed.id).DeletedAt);
    assert(rows(await request('/api/receipts/deleted?limit=500')).some(r=>r.id===independent.receiptId));
    assert(!rows(await request('/api/receipts?limit=500')).some(r=>r.id===independent.receiptId));
    const reportDeleted=await request('/api/reports/spending-by-normalized-description?pageSize=100');assert.equal(cents(reportDeleted.grandTotal),cents(reportBefore.grandTotal)-600);
    await request('/api/receipts/'+independent.receiptId+'/restore',{});
    const restoredItem=await getItem(reviewed.id);const restoredPg=postgres();journal('after-repeat-restore-postgres.json',restoredPg);
    assertRevokedHistory(reviewed,restoredItem,true);assert.equal(restoredItem.normalizationReviewDecisionId,reviewed.normalizationReviewDecisionId);
    assertRestoreDecision(proof,assertIndependentProof(restoredPg,reviewed.id));await finances(independent.receiptId);
    assertCompletionFacts(beforePortable.facts,(await exported('after-repeat-restore.db')).facts);
    assert.equal(cents((await request('/api/reports/spending-by-normalized-description?pageSize=100')).grandTotal),cents(reportBefore.grandTotal));
    await checkpoint('soft-delete-restore',{deleteBefore,restored:await trip(independent.receiptId),reportBefore,reportDeleted,reviewed,restoredItem});
    await browserDraft(future);
    assertCompletionFacts(beforePortable.facts,(await exported('after-browser-completion.db')).facts,future.item.id);
    for(const [name,sha] of Object.entries(completion.files)) assert.equal(hash(path.join(completion.prior,name)),sha,'Prior failed evidence changed');
    for(const [file,sha] of Object.entries(completion.transitionFiles)) assert.equal(hash(file),sha,'Upgrade evidence changed');
    for(const [file,sha] of Object.entries(completion.retainedEvidenceFiles)) assert.equal(hash(file),sha,'Retained completion evidence changed');
    journal('final-postgres.json',postgres());
    journal('lifecycle-completion.json',{...stamp(),status:'passed',completed,prior:completion.prior,...completion.revisions,priorFailureRetained:true,originalFailurePassed:false,newReceiptCreations:0,syntheticReceiptIds:created,baselineFollowupHash:followupHash});
  }
  async function finishBrowserBoundary() {
    assert.deepEqual(await getItem(browserOnly.future.item.id),browserOnly.future.item,'Existing browser purchase changed');
    assertIndependentProof(currentPg,browserOnly.independentId);
    journal('browser-continuation.json',{...stamp(),prior:browserOnly.prior,files:browserOnly.files,completedEarlier:browserOnly.completed,newReceiptCreations:0,rawRunnerLogAvailable:browserOnly.rawRunnerLogAvailable});
    await browserDraft(browserOnly.future);
    assertCompletionFacts(browserOnly.expected.facts,(await exported('after-browser-completion.db')).facts,browserOnly.future.item.id);
    assertRestoreDecision(assertIndependentProof(browserOnly.stop,browserOnly.independentId),assertIndependentProof(postgres(),browserOnly.independentId));
    for(const [file,sha] of Object.entries(browserOnly.files)) assert.equal(hash(file),sha,'Prior browser evidence changed');
    journal('final-postgres.json',postgres());
    journal('lifecycle-browser-completion.json',{...stamp(),status:'passed',completed,prior:browserOnly.prior,...completion.revisions,priorFailureRetained:true,originalFailurePassed:false,newReceiptCreations:0,syntheticReceiptIds:created,baselineFollowupHash:followupHash});
  }
  function captureAllTables(container) {
    const query=sql=>execFileSync('docker',['exec',ready.RunId+'-postgres','sh','-c','PGPASSWORD="$POSTGRES_PASSWORD" exec psql "$@"','sh','-U','postgres','-d','receiptsdb','-X','-q','-A','-t','-v','ON_ERROR_STOP=1','-c',sql],{encoding:'utf8',maxBuffer:64*1024*1024,windowsHide:true});
    const names=JSON.parse(query("SELECT json_agg(schemaname||'.'||tablename ORDER BY schemaname,tablename) FROM pg_tables WHERE schemaname NOT IN ('pg_catalog','information_schema');"));
    assert.deepEqual(names,Object.keys(browserOnly.allTables.tables).sort(),'Public PostgreSQL table inventory changed');
    const quoted=name=>name.split('.').map(part=>'"'+part.replaceAll('"','""')+'"').join('.');
    const sql='BEGIN TRANSACTION ISOLATION LEVEL REPEATABLE READ READ ONLY; SELECT json_build_object('+names.map(name=>"'"+name+"',(SELECT coalesce(json_agg(x ORDER BY to_jsonb(x)::text),'[]') FROM "+quoted(name)+' x)').join(',')+'); COMMIT;';
    return {container,tables:JSON.parse(query(sql))};
  }
  async function browserDraft(future) {
    await pause(65000);
    const require=createRequire(path.join(root,'src/client/package.json')); const {chromium,expect}=require('@playwright/test');
    const browser=await chromium.launch({headless:true}); const context=await browser.newContext({baseURL:ready.Frontend});
    await context.addInitScript(({accessToken,refreshToken})=>{localStorage.setItem('receipts_access_token',accessToken);localStorage.setItem('receipts_refresh_token',refreshToken);},login);
    const page=await context.newPage();
    const apiErrors=[],previewRequests=[];
    page.on('requestfailed',request=>{const url=new URL(request.url());if(url.pathname.startsWith('/api/')) apiErrors.push({path:url.pathname,method:request.method(),transportFailed:true});});
    page.on('response',response=>{
      const request=response.request(),url=new URL(response.url());
      if(!url.pathname.startsWith('/api/')) return;
      if(response.status()>=400 && !(request.method()==='PUT' && url.pathname==='/api/receipt-items/'+future.item.id && response.status()===409)) apiErrors.push({path:url.pathname,method:request.method(),status:response.status()});
      if(url.pathname==='/api/receipt-items/normalization-preview') {
        let body;try {body=request.postDataJSON();} catch {}
        previewRequests.push({path:url.pathname,method:request.method(),status:response.status(),objectBody:!!body && typeof body==='object' && !Array.isArray(body)});
      }
    });
    try {
      await page.goto('/receipts/'+future.receiptId);
      // The field fetches on mount; register before opening the editor so a fast
      // response cannot be missed. Bind readiness to this purchase's exact source.
      const previewResponsePromise=observeResponse(page,response=>matchesPurchasePreview(response,source(location,line)));
      await page.getByRole('row').filter({hasText:line.description}).getByRole('button',{name:'Edit',exact:true}).click();
      const dialog=page.getByRole('dialog',{name:'Edit Item'});
      await dialog.getByLabel('Unit Price',{exact:false}).fill('8.25');
      await dialog.getByRole('button',{name:'Choose product',exact:true}).click();
      const previewResponse=await previewResponsePromise();
      assert.equal(await previewResponse.finished(),null);
      const previewEvidence=await previewResponse.json(); assert(previewEvidence.sourceFingerprint);
      // Review later is enabled even during loading and then clears the choice.
      // Product choices only renders when the successful preview is ready in React.
      await expect(dialog.getByRole('list',{name:'Product choices',exact:true})).toBeAttached();
      await expect(dialog.getByRole('button',{name:'Previous products',exact:true})).toBeVisible();
      await expect(dialog.getByRole('status').filter({hasText:'Loading product choices'})).toBeHidden();
      await dialog.getByRole('button',{name:'Review later',exact:true}).click();
      await expect(dialog.getByText('Needs review — saved with the receipt',{exact:true})).toBeVisible();
      assert.deepEqual(apiErrors,[],'Unexpected browser API errors before competing edit');
      assert(!previewRequests.some(r=>r.method==='POST' && !r.objectBody),'Preview POST had absent or malformed body');
      // The explicit choice captures the observed revision before a competing save.
      await update(await getItem(future.item.id),{unitPrice:4});
      const before=await settled('browser-rejection-settlement'); const portable=await exported('before-browser-rejection.db');
      const response=await journalBrowserSave({page,itemId:future.item.id,journal,
        planned:{utc:new Date().toISOString(),itemId:future.item.id,url:'/api/receipt-items/'+future.item.id,method:'PUT',draftUnitPrice:8.25,intentKind:'defer',expectedItemRevision:future.item.normalizationRevision,sourceFingerprint:previewEvidence.sourceFingerprint},
        click:()=>dialog.getByRole('button',{name:'Update Item',exact:true}).click()});
      journal('after-browser-save-postgres.json',postgres());
      assert.equal(response.status(),409,'Expected genuine stale-revision server rejection');
      const body=response.request().postDataJSON(); assert.equal(cents(body.unitPrice),825); assert.equal(body.normalizationIntent?.kind,'defer'); assert.equal(body.normalizationIntent?.expectedItemRevision,future.item.normalizationRevision); assert.equal(body.normalizationIntent?.sourceFingerprint,previewEvidence.sourceFingerprint);
      const problem=await response.json(); assert(problem.detail);assert.equal(problem.code,'normalization_item_changed');
      await expect(dialog).toBeVisible(); await expect(dialog.getByRole('alert')).toContainText(problem.detail);
      await expect(dialog.getByLabel('Unit Price',{exact:false})).toHaveValue('8.25');
      assert.deepEqual(postgres(),before); assert.deepEqual((await exported('after-browser-rejection.db')).tables,portable.tables);
      const refresh=observeResponse(page,r=>new URL(r.url()).pathname==='/api/trips' && r.request().method()==='GET' && r.ok());
      await dialog.getByRole('button',{name:'Refresh saved receipt information',exact:true}).click();
      const refreshed=await (await refresh()).json(); assert.equal(cents(refreshed.receipt.items.find(i=>i.id===future.item.id).unitPrice),400);
      await expect(dialog).toBeVisible(); await expect(dialog.getByLabel('Unit Price',{exact:false})).toHaveValue('8.25');
      await page.screenshot({path:path.join(out,'browser-draft-after-refresh.png'),fullPage:true});
      await dialog.getByRole('button',{name:'Cancel',exact:true}).click();
      await expect(page.getByRole('row').filter({hasText:line.description})).toContainText('$4.00');
      assert.deepEqual(apiErrors,[],'Unexpected browser API errors');
      assert(!previewRequests.some(r=>r.method==='POST' && !r.objectBody),'Preview POST had absent or malformed body');
      journal('browser-api-observations.json',{apiErrors,previewRequests});
      await checkpoint('browser-rejection-cache-refresh-draft',{request:body,status:response.status(),problem,refreshed,interception:false});
    } catch(error) {await page.screenshot({path:path.join(out,'browser-failure.png'),fullPage:true});throw error;} finally {if(!fs.existsSync(path.join(out,'browser-api-observations.json'))) journal('browser-api-observations.json',{apiErrors,previewRequests});await browser.close();}
  }
  },captureFailure:async error=>{
    journal('failure.json',{...stamp(),completed,syntheticReceiptIds:created,error:safeError(error)});
    try {journal('safe-stop-postgres.json',capturePostgres());} catch(captureError) {journal('safe-stop-postgres-error.json',safeError(captureError));}
    if(token) await captureExport('safe-stop.db').catch(exportError=>journal('safe-stop-export-error.json',safeError(exportError)));
    else journal('safe-stop-export-unavailable.json',{reason:'No authenticated token; PostgreSQL captured independently'});
  }});
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) await main();
