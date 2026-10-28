# Offline experimental candidate plan and decision parity

## Offline exhaustive shortlist experiment

`ExhaustiveShortlist.Prepare(purchase, revision, taxonomy, model)` creates an
immutable offline plan. It asks one independent typed Noul about **every category's
complete path**, including broad categories and every competing root. Each question
includes all names and definitions on that path. No root answer controls whether
another question is asked. The input uses the existing candidate's validation and
1,024-category/16-level bounds. This is a C# API; no CLI or production registration
was added. It has no transport, credential access or live inference.

Planning sorts category IDs ordinally, then fills batches in that order with at most
16 questions and 64 KiB of serialized UTF-8 JSON per batch. A complete path that
cannot fit fails planning explicitly; no description or definition is truncated.
The SHA-256 plan binding includes the purchase, revision, complete sorted taxonomy,
model, question/criteria, evidence rule, retention threshold and bounds. Every question
ID includes that binding. Reordering the taxonomy preserves the plan and batches.

`plan.Aggregate(responses)` requires one raw response for each batch, in batch order.
Each response must contain exactly `model`, `answers`, and `usage`; every planned
question ID must appear exactly once as `{ "type": "noul", "noul": probability }`.
Usage requires nonnegative integer `input_tokens` and `output_tokens`. Unknown,
missing or duplicate fields, invalid probabilities, failed/missing batches, mismatched
models, and answers from another plan produce a failure with no partial shortlist.
The caller must represent a transport failure as a missing/invalid response, never
as fabricated low probabilities. The response parser bounds each response to 1 MiB.

All answers above the fixed candidate rejection boundary of 0.3 remain plausible,
including uncertain answers. Descendant retention never depends on ancestor scores.
The reducer keeps maximal retained paths and expands their full ancestor closure;
retained ancestors are represented in that closure. More than 32 maximal paths or
128 closure categories produces `shortlist-overflow` abstention with no top-k
truncation. No retained path produces `no-plausible-path`. Success returns only an
existing `CandidateVerificationPlan`, which still needs independent support/context,
purchase-evidence and unlisted-alternative judgments for the candidate verifier.
It is neither a classification nor authorization to assign a category.

Exhaustive enumeration proves that every supplied category received a question;
it does **not** prove semantic recall. A wrongly low plausibility answer can still
discard a correct category. No model call or holdout tuning has been performed.
Synthetic tests cover competing roots, uncertainty, ancestor closure, both overflow
bounds, deterministic 309-category batching, complete definitions and malformed,
missing, duplicate and replayed responses.

The byte and question limits are **offline memory/payload bounds, not model token
limits**. Before any separately authorized live evaluation, verify the pinned model's
current per-question, shared-state, request and output token limits using its documented
tokenizer/accounting; check every batch against those limits and reject or replan
explicitly if one does not fit. Verify current pricing and reserve an enforceable
experiment budget (including verifier calls, retries and output). This planner does
not estimate tokens or dollars, does not enforce a $5 spending cap, and has made no
inference charges. Live compatibility and model quality remain unverified.

This adapter invokes `SupportedCategoryCandidate.Plan` and `SupportedCategoryCandidate.Decide`
from Application directly. All question wording, criteria, evidence rules and thresholds
come from that candidate. No candidate algorithm is copied. It creates no application
host, provider, HTTP client, database or inference request, reads no configuration or
credentials, and cannot activate a policy or persist assignments.

From the repository root:

```powershell
dotnet run --project tools/Receipts.JevCandidateParity -- tools/Receipts.JevCandidateParity/fixtures/synthetic-fruit.json
dotnet test tests/Application.Tests/Application.Tests.csproj --filter FullyQualifiedName~JevCandidateParityTests
```

Build/restore may access package feeds. After building, obtain clean JSON stdout
without build/restore by running the DLL:

```powershell
dotnet tools/Receipts.JevCandidateParity/bin/Debug/net10.0/Receipts.JevCandidateParity.dll tools/Receipts.JevCandidateParity/fixtures/synthetic-fruit.json
```

Supply an explicit UTF-8 JSON fixture with `id`, `purchase`, `taxonomyRevision`,
`taxonomy`, `shortlistedIds` and a typed `judgment`, as shown in the entirely synthetic
example. Use independently sanitized or synthetic data: the adapter does not sanitize
or certify it. Output contains purchase text and the full taxonomy; handle it like
the input. The CLI reads only the named file, with an 8 MiB input bound. Unknown JSON
properties and missing constructor fields (including every probability) are rejected
to catch schema mistakes. Include nullable fields explicitly as `null` when absent.
Candidate input bounds still apply.

Judgments must cover exactly the expanded verification path IDs, including each
ancestor. In the example `apple` expands to `apple` and `food`; `tools` remains in
the taxonomy for the supplied unlisted-alternative judgment but is not a verified
path. The caller supplies the shortlist; this adapter performs no retrieval,
shortlist generation or recall measurement. It cannot establish that the shortlist
covers the correct category. A low supplied unlisted-alternative probability is
an assumption of this offline evaluation, not independent proof of completeness.

## Fingerprint contract

The plan parity command defines `candidate-offline-plan-v1`, an **offline snapshot**,
not a provider request. The separate offline wire API below provides typed request
serialization and raw response parsing; it does not change this snapshot contract.
The output `plan` contains the format, candidate policy version, evidence rule,
support/rejection thresholds, four candidate question/criterion templates and the
actual immutable plan (purchase, taxonomy revision, taxonomy and expanded paths).
Path templates are interpreted against each full path in the plan; they are not
bound into API question IDs or a wire payload here.

`planSha256` hashes the compact UTF-8 camelCase JSON serialization of that output
`plan` object using this adapter's read-only `JsonSerializerOptions`.
`decisionSha256` hashes the same serialization of the complete output `decision`.
Both are lowercase SHA-256 hex. Fixture id, filename, timestamps, environment and
judgment inputs are excluded. Judgment changes affect the decision hash only if
the resulting decision changes; these hashes are not a full evaluation audit log.
Keep the original fixture separately when reproducing an evaluation.

Preserve taxonomy input order: it is retained in the snapshot even if two orders
have equivalent decision semantics. Expanded paths are ordered by the candidate.
Shortlist duplicates/order that expand to the same paths produce the same snapshot.
These are .NET JSON fingerprints, not canonical cross-language hashes. Record the
git revision and SDK/runtime alongside comparisons. Version labels alone do not
identify the code. Matching hashes imply neither semantic equivalence between
different policies nor model accuracy.

Successful execution exits 0 for selection, abstention and candidate
`invalid-judgment` failures, recording them distinctly. Invalid plan inputs,
malformed/oversized/unreadable files and invalid CLI usage exit 2. Error messages
do not echo fixture content or exception text.

This is experimental plan/reducer parity only. The separate production parity
adapter calls production question serialization and policy; its question hash and
this plan hash have different contracts and must not be compared as equal requests.
This plan parity command does not establish production/provider parity, raw response parsing,
transport behavior, authorization, model quality, shortlist recall or activation
readiness. There is no inference capability or production registration.

## Offline typed wire API

`CandidateWire.Prepare(plan, model)` creates a deterministic TypeSafe-shaped request
from the actual candidate plan. The only supported evaluation model is the pinned
`jev-1.14.0`, which must be supplied explicitly. Aliases, other versions and missing
model names are rejected before serialization. The adapter has no credentials,
HTTP, configuration or registration.
Its immutable `Payload` is the complete UTF-8 JSON text that an independently
authorized evaluator could send. This change supplies a C# API only; the CLI above
still evaluates typed fixture judgments and does not send or parse wire responses.

```csharp
CandidateVerificationPlan plan = SupportedCategoryCandidate.Plan(
    syntheticPurchase, taxonomyRevision, syntheticTaxonomy, shortlistedIds);
CandidateWireRequest request = CandidateWire.Prepare(plan, explicitEvaluationModel);
string exactPayload = request.Payload;
CandidateWireResult result = request.ParseResponse(savedRawResponse);
```

There are two global Noul questions (purchase evidence and an unlisted alternative),
plus independent support and context Nouls for each expanded full path, including
ancestors. All wording, criteria, evidence rules and reducer thresholds come from
`SupportedCategoryCandidate`; path questions carry their exact full path in their
instructions. State includes the purchase, full taxonomy, revision and verified
paths so the unlisted-alternative judgment can inspect categories outside them.
The candidate's constants are unchanged; no holdout data was used.

`PlanSha256` is lowercase SHA-256 over compact camelCase JSON containing
`{format: "candidate-typesafe-wire-v1", model, snapshot}`. `snapshot` has exactly
the `CandidatePlanSnapshot` contract described above. Every question ID starts
with that full hash and ends with `purchase`, `unlisted`, or
`path_<zero-based expanded-path index>_support/context`. Thus answers from changed
evidence, taxonomy, revision, expanded paths, questions, policy or requested model
cannot be replayed unchanged against a different prepared request. Preserve the
exact payload, raw response, code revision and chosen model for reproducibility.
This is deterministic binding, not cryptographic proof of provider provenance:
someone able to edit answers can also rewrite IDs. Equivalent taxonomy orders
remain distinct snapshots, as in plan parity.

`ParseResponse` accepts only the exact response model requested. The public API
allows a resolved model name to differ from an alias; aliases are rejected at
preparation here to avoid that mismatch. Root fields must be
exactly `model`, `answers`, `usage`; each answer must have exactly `type: "noul"`
and a finite numeric `noul` between 0 and 1. Answers must match the exact question
ID set. Missing, extra and duplicate properties are rejected at each object level,
as are missing, fractional, negative or overflowing token counts. Requests are
bounded to 8 MiB and responses to 1 MiB of UTF-8 with nesting at most 32.
These byte bounds do not enforce the model's 64k request or 32k per-question token
limits. Token-limit validation before any provider dispatch is a required future
evaluation-harness responsibility; this offline API is not provider-ready.

Valid answers map to a `CandidateJudgment` and call the existing candidate reducer.
Selections, abstentions and failures remain distinct. Invalid raw responses return
`Decision.Failure = "invalid-response"`, with no judgment, category or usage; they
do not become negative evidence or abstentions. Errors never echo response content.
The adapter cannot persist or authorize the returned category as an assignment.

Request/answer shapes were checked against the public
[TypeSafe OpenAPI contract](https://api.typesafe.ai/openapi.json) on 2026-10-28.
No live API inference was performed. Synthetic tests exercise serialization,
binding, mapping, malformed responses and the offline assembly boundary. This is
wire/reducer plumbing evidence only: it establishes no model accuracy, real
shortlist recall, provider provenance, transport behavior or activation readiness.
A live evaluation harness, separately authorized data/budget and an independent
holdout assessment remain required before any activation decision.
