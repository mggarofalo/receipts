# Offline-normalized portable backup round trip runner

This is an acceptance harness, **not executed acceptance evidence**. It uses the actual Aspire AppHost, actual HTTP endpoints, real PostgreSQL and Playwright Chromium with no HTTP interception. No provider credentials are required. The browser package resolves from `src/client/node_modules` using the existing lockfile. Install that locked dependency set before running. Python 3's standard SQLite module supplies independent read-only artifact snapshots.

The semantic oracle is `test-data/offline-normalization-acceptance.json`. It must remain separate from the blinded normalizer and decision reviewer. The runner never generates a reviewed decision manifest. Arrangement uses only entry facts and fragment names; post-import assertions use gold. Fixture keys are bound to actual application IDs in a private file. No SQL arranges application data.

All output paths must be outside this checkout. Use a directory with inheritance disabled and an ACL restricted to the operator. The runtime launcher already restricts its private directory; put evidence below it. Tokens remain in process memory; the changed synthetic administrator password is retained only in the runtime directory. Never print or commit `secrets.json` or `acceptance-password.json`.

## Runtime

The existing private runtime project is at:

`C:/Users/mggar/AppData/Local/Temp/receipts-offline-acceptance-20261024`

Launch only after concurrent .NET builds have finished. Its launcher writes its log privately and blocks for the lifetime of the AppHost. Launch it with PowerShell `Start-Process -WindowStyle Hidden`, recording the helper PID. Pass the candidate worktree as `-CandidateRoot` and `-Installation a` for the source. Start a second runtime with `-Installation b` only when ready for the fresh restore. Find the single `<runId>-ready.json` inside the newly created private runtime directory; there is no `ready.json`. Do not use a prior runtime's ready file after its process exits.

For Node HTTPS requests, set `NODE_EXTRA_CA_CERTS` only in the runner process environment to the runtime's matching `localhost-trust.pem` public certificate. Keep normal TLS verification enabled.

The harness sets `ChatGpt__Enabled=false` (the API binds `NormalizationAiOptions` from `ChatGpt`). `NormalizationAi__Enabled` is ineffective. No assistance preferences or provider runs are arranged. After initial assertions, a genuine unapproved operation preview is created through POST `/api/normalization-catalog/operations` for the unresolved BAKERY SPECIAL source. It previews an exclusion without approving it, creating a target/mapping or changing purchases. `operation-fixture.json` retains the actual request, response and PostgreSQL frozen membership. Catalog workers and the approved-mapping resolver have no general configuration disable switch. Before A freeze there are no remembered mappings or approved historical operations for these workers to apply. `freeze-check` compares all portable state and rejects mutation. This is an observed freeze guard, not a claim that every hosted service is disabled. Embedding generation can change rebuildable projections that portable backups deliberately omit.

Container names are task-specific. Model mount annotations alone do not prove isolation: images declaring `VOLUME` produce anonymous Docker volumes. Inspect each exact model container name with `docker inspect`, retain actual `.Mounts`, and assert no bind mounts or named volumes shared with another installation. Retain PostgreSQL container ID and mount source for restart evidence. Cleanup uses `docker rm -f -v <exact owned container names>` after retaining evidence; never remove another acceptance task's containers or volumes, including `receipts-chatgpt-acceptance-6f6aa5a7`.

## Phases

From the candidate worktree in PowerShell, set private path variables, then run:

```powershell
$runner = './tools/acceptance/offline-normalization/run.mjs'
$runtimeA = 'C:/Users/mggar/AppData/Local/Temp/receipts-offline-acceptance-20261024/<actual-source-runId>'
$evidenceA = Join-Path $runtimeA 'evidence'
node $runner arrange "runtime=$runtimeA" "out=$evidenceA"
node $runner freeze-check "runtime=$runtimeA" "source=$evidenceA" "out=$(Join-Path $runtimeA 'freeze-check')"
```

`arrange` requires an empty receipt database, creates account/card/template, enters r1 using actual browser form events and observes the actual POST, enters r4 through receipt-then-items APIs and the remainder through aggregate APIs. Every request is real. Before export, it requires each explicit fragment to have its own reviewed identity and exact fragment name, and every other initial item (including template provenance) to remain pending. It saves these initial API projections in `initial-state.json`. It exports A through the application, captures SHA-256, factual and portable-state baselines, ID bindings and a freeze record. A becomes read-only. Interrupted arrangement is deliberately not resumed automatically; use a new task runtime to avoid duplicating receipts. A phase output directory must be new because evidence files are created with exclusive writes.

Only A and a separately frozen synthetic operator-policy packet go to the offline proposer/reviewer pipeline, with their hashes and independently authorized review process. Do not give the proposer the gold, bindings, expected outcomes or this oracle's output. Keep the original A unchanged. Have that separate pipeline produce B, its reviewed decisions, provenance and independent validation. The runner requires an explicit recorded B hash; it neither copies decisions out of gold nor regenerates B from a database.

The exact reusable subset is a synthetic scenario preference selected by the test
author/root from the preregistered fixture, not a claim about the real owner's
choices. A fact-only blind run cannot infer that preference. The policy-conditioned
run supplies eligible exact scopes before a new blind proposal and independent
review; identity/whole-scope consistency remain independent decisions. All other
choices stay one-off. Keep earlier drafts and gold byte-identical, freeze their
hashes and record the first attempt as a protocol mismatch, not a pass. The private
freeze ledger is assessment evidence and must not accompany the blind packet.

Build the private policy from production `inspect` output and arrangement bindings;
this script never reads the oracle or copies targets, assignments, totals or item
IDs into the packet. Selection is an explicit test-author input. It copies source
fingerprints rather than reimplementing application canonicalization. Use a new
output path under the ACL-restricted runtime:

```powershell
node ./tools/acceptance/offline-normalization/operator-policy.mjs `
  "source=$evidenceA/A.db" "inspection=<private production inspection.json>" `
  "bindings=$evidenceA/bindings.json" eligible=r1-beef,r3-code,r4-code `
  "out=<new private operator-policy.json>"
node --check ./tools/acceptance/offline-normalization/operator-policy.mjs
node --test ./tools/acceptance/offline-normalization/operator-policy.test.mjs
```

Freeze the resulting packet hash and creation time before dispatching fresh agents.
Give them only A and that packet, not this README, selection keys, original drafts,
bindings or ledger. The builder checks inspection/source byte binding and projects
only normalized source tuples; production inspection must itself be trusted and
retained privately. This synthetic policy authorizes no real-data reuse. For real
data, the owner/operator must explicitly supply reuse authority independently of
the synthetic scenario and independently of semantic correctness.

```powershell
$b = 'C:/private/offline-output/B.db'
$bHash = '<SHA-256 recorded by offline pipeline>'
$restoreA = Join-Path $runtimeA 'restore-source'
node $runner restore "runtime=$runtimeA" "source=$evidenceA" "out=$restoreA" "b=$b" "sha256=$bHash" target=source
$runtimeB = 'C:/Users/mggar/AppData/Local/Temp/receipts-offline-acceptance-20261024/<actual-fresh-runId>'
$restoreB = Join-Path $runtimeB 'restore-fresh'
node $runner restore "runtime=$runtimeB" "source=$evidenceA" "out=$restoreB" "b=$b" "sha256=$bHash" target=fresh
```

`restore` confirms A's hash, source freeze or fresh-target emptiness, B factual equality to A and SQLite integrity/foreign keys. Before importing, it independently checks B's product identities/names, review buckets, excluded targets/reasons and exact reusable mapping scopes/targets against gold, recording `pre-import-gold.json`. It uploads the exact B twice, checks factual preservation and repeated-import portable-state equality, then asserts independent product identities/names, review buckets, report groups/subtotals, history dates and nonempty raw/normalized API search. It captures real receipt/list browser screenshots and API responses. These checks are deliberately strict. The importer regenerates historical operation `Revision` on every restore. The runner requires a new nonempty UUID, exact `restored`/`restored_observation` state and documented error detail, and exact preservation of every other operation field and frozen member. Repeated semantic comparison ignores only that validated operation revision, preserving strict comparison of all other portable rows and fields.

Restart: keep each harness and its PostgreSQL container running. `restart-api` writes a one-shot private request which the running harness consumes using Aspire's `ResourceCommandService` and `KnownResourceCommands.RestartCommand` on the actual `api` resource. It requires changed API start identity, waits for health, compares exact PostgreSQL container ID/mounts and durable rows, then reruns API/browser expectations. Never stop/relaunch `Start-Harness.ps1` to establish persistence: that provisions a different database. The harness must have been built/launched with the current private `Program.cs` for the control to exist. The older read-only `verify-restart` phase still requires an external restart event.

```powershell
node $runner verify-restart "runtime=$runtimeA" "source=$evidenceA" "out=$(Join-Path $runtimeA 'restart-evidence')"
node $runner restart-api "runtime=$runtimeA" "source=$evidenceA" "restore=$restoreA" "out=$(Join-Path $runtimeA 'controlled-restart')"
node $runner restart-api "runtime=$runtimeB" "source=$evidenceA" "restore=$restoreB" "out=$(Join-Path $runtimeB 'controlled-restart')"
node $runner reject-corrupt "runtime=$runtimeB" "out=$(Join-Path $runtimeB 'corrupt-evidence')" "b=$b" "sha256=$bHash"
node $runner sensitivity "runtime=$runtimeB" "source=$evidenceA" "out=$(Join-Path $runtimeB 'sensitivity-evidence')" "b=$b" "sha256=$bHash"
node $runner followups "runtime=$runtimeA" "source=$evidenceA" "out=$restoreA"
```

Followups are authorized only after frozen restores and restart checks on both installations have been assessed. The code requires a successful source restore record before entry. It creates seven supported aggregate purchases, checks three remembered scopes versus four pending cases, and expects 22 items: 13 verified, 8 pending, 1 excluded. The gold records USD 84.80 after followups. Existing A remains unchanged. `reject-corrupt` makes separate truncated, dangling-target, mapping fingerprint/revision, review fingerprint and unsupported-evidence copies. Each must return HTTP 400 with problem detail and leave both exported portable state and directly queried PostgreSQL rows unchanged. Missing corruption prerequisites fail explicitly.

`restore` now queries real PostgreSQL independently with `docker exec <exact task name> sh -c 'PGPASSWORD="$POSTGRES_PASSWORD" exec psql "$@"' sh ...` using the container's local socket. The password stays in the container; it is never read by the host or printed. It checks item facts, current/reviewed source fingerprints, decision/assignment evidence, mapping revisions and the exact three scoped reusable mappings. Imported operations, when present, must be `restored` with `restored_observation`; approve/resume/cancel/undo must each reject with 409 without changing durable rows. The genuine arranged operation must be present; an empty operation corpus fails. The controlled restart reruns inspect-only mutation rejection and durable-row checks. Supported APIs/browser assertions cover raw and normalized purchase search, ordered unit prices with exact receipt links, receipt/list review counts and report amounts split by currency/review status. USD is the only supported currency in this corpus.

`sensitivity` starts with passing restored API projections, changes one turkey assignment to beef in a separate projected object and requires the same independent identity oracle to reject it. It changes a unit price in a separate SQLite copy and requires the same pre-import factual comparison to reject it. These are oracle-sensitivity checks; the deliberately incorrect copies are never imported. None of these additions constitute executed acceptance evidence until their actual phases pass.

## Artifact ledger and remaining gates

If an exact-B restore completed both HTTP 200 uploads and captured both exports,
but a later assertion failed, use `restore-resume` with a new evidence directory.
This phase performs no imports or restart, and refuses a password reset. Retain
the entire original attempt, including its failure evidence:

```powershell
node $runner restore-resume "runtime=$runtimeA" "source=$evidenceA" `
  "prior=$restoreA" "out=$(Join-Path $runtimeA 'restore-source-resume')" `
  "target=source" "b=$b" "sha256=$bHash"
node --test ./tools/acceptance/offline-normalization/restore-resume.test.mjs
```

Resume requires two retained successful upload attempt/response records matching
the exact runtime, target, artifact path and SHA-256, and their original exports.
It rechecks original A/baseline, original source freeze (or empty fresh target),
B facts/provenance/gold, restored operation fencing and repeated semantics. The
current export must exactly equal the second retained restore. Every API/browser
gold check and PostgreSQL proof/409 mutation rejection reruns. PostgreSQL and
portable state must remain unchanged. `restore.json` points to the original
uploads and new verification evidence and records zero new uploads. The original
failed attempt remains failed evidence; only the completed resume establishes
the combined restore result.

Browser verification respects the production global default of 100 requests per
minute with four sliding-window segments: it waits 65 seconds after dense API
checks and at least 12 seconds between page navigations. It also cools a full
window before and after the API oracle used during browser verification. Requests
use the real server with ordinary TLS and rate limiting. Browser response logs
retain all API statuses; any browser HTTP error fails verification, without
interception, retries or skipped assertions. Resume failures retain a checkpoint
and API request ledger. These waits occur asynchronously in the runner.

| Artifact | Meaning |
| --- | --- |
| Runtime `source.json`, model/ready manifests, runtime.log | exact candidate revision/dirty state, resource declarations and endpoints |
| `bindings.json` | actual receipt/item/account/card/template IDs |
| `A.db`, `baseline.json`, `freeze.json` | immutable source hash, raw facts, complete portable state and freeze precondition |
| Separate offline B/decisions/provenance | exact externally reviewed transformation; never synthesized from gold |
| `before-restore.db`, `restored-1.db`, `restored-2.db`, `restore.json` | exact-artifact uploads, API oracle and repeat state comparison |
| PNGs | actual rendered receipt/list evidence; screenshots alone do not establish semantic correctness |
| `after-restart.json` plus controlled Aspire result | changed API process/start identity and unchanged PostgreSQL container/mounts/state; older verifier still needs external event |
| corrupt before/after files and `corrupt.json` | six rejection/rollback cases against actual import API and PostgreSQL |
| `sensitivity.json` and separate factual tamper copy | independent semantic/factual oracle sensitivity, with original B hash retained |
| `followups.json` | supported post-restore entry results and read projections |

Still required before overall acceptance: execute these gates against the integrated runtime and resolve any response/selector mismatches; execute the genuine preview fixture and inspect-only assertions; cache refresh and unsaved-draft behavior; edit/revoke/merge/delete-restore cases. The portable factual snapshot excludes normalization columns/tables but preserves every other table/column, including unknown additions. The legacy backup migration and other existing integration gates remain complementary requirements. Semantic sensitivity currently mutates a restored API projection; a valid but semantically wrong artifact restore is additional evidence, not claimed here.

No acceptance phase has been run against the integrated application when this runner is added. Browser selectors, current response shapes and repeated-import projection stability require actual execution. Failures stop their phase and cannot be reported as passing. A successful phase record is not overall acceptance or permission to mutate hosted data.

## Post-restore lifecycle gates

`lifecycle.mjs` is a separate mutating acceptance driver. Run it only after the
frozen offline A-to-B review, both exact-B source/fresh restores (two imports
each), both controlled `restart-api` checks, and seven followups created after
both restarts. It requires those successful records and runs on the source task
runtime. It does not read or rewrite gold.

```powershell
# In a short-lived runner process; do not persist certificate configuration.
$env:NODE_EXTRA_CA_CERTS = Join-Path $runtimeA 'localhost-trust.pem'
node ./tools/acceptance/offline-normalization/lifecycle.mjs `
  "runtime=$runtimeA" "source=$evidenceA" `
  "sourceRestore=$restoreA" "freshRestore=$restoreB" `
  "sourceRestart=$(Join-Path $runtimeA 'controlled-restart')" `
  "freshRestart=$(Join-Path $runtimeB 'controlled-restart')" `
  "followups=$restoreA" "out=$(Join-Path $runtimeA 'lifecycle-evidence')"
Remove-Item Env:NODE_EXTRA_CA_CERTS
```

Keep ordinary TLS verification enabled. Output must be a new directory below the
ACL-restricted private runtime. A failed run retains `failure.json` and incremental
creation records; inspect them before deciding recovery rather than blindly
rerunning mutations. No automatic cleanup hides partial arrangement.

Before adding data, the driver compares the entire current portable state with
`after-followups.db`, requires 22 items / 15 receipts, and retains that baseline
and independent PostgreSQL rows. Mutations use additional isolated synthetic
records. Every checkpoint preserves original portable rows, original purchase
facts/proof, source A and followup hashes, and PostgreSQL container/mount identity.
Coverage and spending totals/review buckets reconcile to independently queried
live PostgreSQL facts, including receipt soft deletion.

The gates cover provider-disabled receipt-first manual entry; financial edits
that preserve authority; parent-location edits that invalidate scoped authority
without changing money; stale item/mapping revisions without partial facts, proof,
replay or audit writes; revocation that retains historical links and independent
review while stopping future reuse; an intentional same-product merge of isolated
declarations; and receipt soft-delete/restore.

The actual browser chooses Review later, observes a real competing API edit, and
must receive HTTP 409 on save. It checks that the unsaved price remains in the
open dialog, clicks the application's refresh action, observes the real GET with
the competing saved price, and checks that the draft survives and the saved row
reflects refreshed data after cancellation. No HTTP interception is used.

`lifecycle.json` is written only after every gate passes. Checkpoint JSON, SQLite
exports, PostgreSQL rows and browser PNGs are private evidence. The following
checks exercise only syntax and standalone negative-oracle assertions, without
runtime access or application mutations:

```powershell
node --check ./tools/acceptance/offline-normalization/lifecycle.mjs
node --test ./tools/acceptance/offline-normalization/lifecycle.test.mjs
```

These checks do not establish executed lifecycle acceptance. Actual API and
selector mismatches fail explicitly and require assessment before claiming a pass.
