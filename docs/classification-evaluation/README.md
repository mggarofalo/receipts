# Offline classification evaluation

RECEIPTS-1062 prepares a bounded experiment. It does not establish Jev quality,
approve production integration, or change the application's taxonomy. No live
authentication, inference, real receipt input or credential inspection was used
to construct this harness.

The dependency-free Node 24 scripts follow the existing
[normalization corpus](../normalization-evaluation/README.md) conventions:
invented fixtures, explicit splits, labels excluded from payloads, no retries,
and failure/unattempted denominators. They do not reuse the ChatGPT runtime:
that transport has a different authentication and response contract.

## Corpus and policy

[corpus.v1.json](corpus.v1.json) contains 24 invented descriptions: 8 tuning and
16 held-out rows (15 distinct held-out source groups). Repeated pear purchases
stay in one split and generate one request. Source groups represent the same
fictional exact source; related variants must be assigned the same group when
they belong to that source. Normalized repeated text also cannot cross splits.
Normalization is a leakage check, not a production identity rule. Shared category
concepts across splits are intentional. Human review must still catch disguised
paraphrases and related sources the structural checks cannot identify.

Labels and definitions are proposed agent-authored assumptions, written before
model output. They are not human-adjudicated truth. Owner review of dairy/plant
boundaries, broad categories, and ambiguous text remains required before quality
acceptance. Every case states its rationale. The nine category keys are finite
fixture-local identities, not production category UUIDs. A fixture revision UUID
pins this synthetic tree; corpus SHA-256 also detects any content change.

The central `policy` and `payloadFor` in
[the runner](../../scripts/classification-evaluation.mjs) pin model
`jev-1.14.0`, question policy `classification-questions-v1`, and thresholds.
Each request carries only description, taxonomy revision, shared category
definitions/paths, Choice criteria, and a companion fit Noul. Each independent
question sees category meanings in shared state. Expected labels, rationale, split,
source group and case ID remain local. No arbitrary text generation, product
identity merge, production database, financial field or real source is involved.

| Validated provider judgment | Application outcome |
| --- | --- |
| Fit probability <= 0.3 | Abstain: no fit |
| Fit probability > 0.3 and < 0.7 | Abstain: unresolved fit |
| Fit probability >= 0.7 | Select the supplied Choice category; derive its one ancestor path in code |
| Transport failure, timeout, invalid response, unknown category, model drift | Failure; stop later requests |

Broad categories are valid direct answers. Choice always selects a candidate;
the separate Noul permits no-fit. Thresholds are provisional and uncalibrated;
confidence is recorded for inspection, never treated as a correctness guarantee.
Every supplied candidate is selectable in this fixture revision.

## Offline commands

From the repository root:

```powershell
node --test scripts/classification-evaluation.test.mjs
node scripts/classification-evaluation.mjs dry-run --split heldout --limit 6 --max-requests 6 --max-spend-usd 0.02 --out classification-dry-run.json
```

Dry-run reads checked-in JSON only, does not access credentials and makes zero
network calls. Its output contains the exact six JSON request bodies, corpus
hash, policy, selection counts and estimates. Output must be a new filename;
an existing file is rejected before dispatch. First six held-out descriptions:
`LF COW MILK 2L`, `OAT DRINK BARISTA`, `DAIRY ASSORTMENT UNSPECIFIED`,
`PARKING TICKET`, `MISC XQ7`, `CHDR CHS BLOCK`. These cover animal/plant,
broad, no-fit, ambiguity and abbreviation cases. Selection is deterministic;
the public fixed order is a smoke probe, not a statistical sample.

Example request state excerpt (the full taxonomy and exact Choice/Noul definitions
are in dry-run output):

```json
{"description":"LF COW MILK 2L","taxonomyRevision":"3d58027a-4888-4abb-8a95-6380465be5ee"}
```

## Prepared live command — not executed

Target: the operator's local Node process, synthetic input only. Follow
[Jev setup](../jev-setup.md) for the canonical `TYPESAFE_API_KEY` setting and
server-side authentication check. Provision the API's existing .NET User Secrets
with the documented `dotnet user-secrets set TYPESAFE_API_KEY ... --project
src/Presentation/API` command. Do not paste a key in chat, dump environment
variables, or run `dotnet user-secrets list`.

The explicit live-only `--api-user-secrets` switch reads the API project's
`UserSecretsId`, locates its standard local `secrets.json`, and selects only
`TYPESAFE_API_KEY`. It never logs the object, key, filesystem paths or parser errors.
An existing `TYPESAFE_API_KEY` environment setting takes precedence, including an
empty setting which fails as missing. No second credential provisioning is needed.
Dry-run ignores this switch without reading environment values or secret storage.
The project must declare a literal `UserSecretsId`; missing or invalid identity
fails safely. The resolver follows [Microsoft's documented storage
locations](https://learn.microsoft.com/en-us/aspnet/core/security/app-secrets?view=aspnetcore-10.0):
Windows `%APPDATA%/Microsoft/UserSecrets/<id>/secrets.json`; Linux/macOS
`~/.microsoft/usersecrets/<id>/secrets.json`. Local User Secrets are not encrypted
production storage. Deployment harnesses can instead inject the canonical process
environment key through their secret manager and omit the switch.

After the owner reviews payloads, provisions the key, and authorizes this experiment:

```powershell
node scripts/classification-evaluation.mjs live --api-user-secrets --split heldout --limit 6 --max-requests 6 --max-spend-usd 0.02 --out classification-live-probe.json
```

This is at most **6 inference requests**, sequential, 10-second timeout each,
no retries, no redirects, no model fallback and no discovery request. Failed
attempts consume the request allowance. The whole plan is rejected before any
send if its count exceeds the cap. A source group is evaluated once. The full
held-out set is 15 requests; it needs a separate reviewed command/budget and new
output filename. A configured key alone is not inference authorization.

Official [models and pricing](https://docs.typesafe.ai/models) inspected
2026-10-25 list $0.042 per million input tokens, output free, and a default 64k
request context for this concrete model. Long context is never enabled.
At 64,000 input tokens per request, six requests reserve **$0.016128**, within
the proposed **$0.02** allowance. Dry-run also estimates ordinary payload size
using characters/4; that is not a tokenizer measurement. The budget flag enforces
this conservative context reservation before dispatch, not a provider-side
dollar billing limit. Price changes, token accounting and other account activity
are outside this harness; confirm pricing before authorization and use provider
account limits if a strict dollar ceiling is required. Validated responses record
actual token counts and cost at the pinned rate. Failed requests can have unknown
usage; partial measured cost must never be called the total bill.

Request/response syntax follows [OpenAPI 0.2.0](https://api.typesafe.ai/openapi.json),
`POST https://api.typesafe.ai/v1/systemone`, bearer auth. The
[Choice](https://docs.typesafe.ai/primitives/choice) and
[Noul](https://docs.typesafe.ai/primitives/noul) primitives supply judgments;
hierarchy selection policy stays in code. Verify public docs again before live
use if the protocol changes. SDK default retries are avoided by using fetch.

## Evidence and acceptance

Reports contain synthetic case IDs, outcome, validated category/path,
fit/selected-category probabilities, confidence, latency, usage, hashes and
versions. They exclude keys, exception messages and raw provider bodies.
Dispatch intent is saved before each send and outcomes after it. A crash or
filesystem failure can leave a partial/empty report: unknown progress/usage,
never evidence of zero requests. Do not restart a failed experiment automatically.

Metrics count selected distinct source groups. Coverage is classified/total;
selective accuracy is correct categories/classified; total agreement includes
correct abstentions over total. Reports also show direct broad answers correct,
harmful descendants of a broad expected category, wrong branches, over-broad
ancestors, no-fit misclassification, correction count, abstention, failure and
unattempted counts. Failures never become abstentions. Empty denominators are
null. The leave-unclassified baseline reflects zero remembered mappings for
unseen source groups; it is not a benchmark of the current production resolver.

Proposed readiness gates: zero protocol/hierarchy validation failures, zero
harmful narrow or no-fit classifications in the smoke probe; inspect every
disagreement manually before expanding. A small synthetic probe cannot establish
accuracy, calibration, production coverage or a go decision. Production go/no-go
belongs to RECEIPTS-1056 after owner-adjudicated labels, a fresh held-out set,
agreed coverage/precision and correction-effort targets, and measured latency and
cost. If questions/definitions/thresholds change after inspecting held-out outputs,
those cases become tuning data; add untouched source groups for the next claim.

RECEIPTS-1055 may request provisioning only after this prepared harness and
RECEIPTS-1054 authentication setup are reviewed. No production adapter is accepted
by offline checks. This work changes no application runtime, OpenAPI or CI.
