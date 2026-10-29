# Offline Jev production question and decision parity

The original single-fixture mode references the shipped Infrastructure/Application assemblies and calls
`JevSuggestionQuestions.Serialize` and the hierarchical `CategorySuggestionPolicy.Decide`
directly. There is no copied question wording or decision policy. It instantiates
no provider, HTTP client, application host, database, or model. It reads only the
explicit fixture file and writes one JSON result to stdout; it reads no credentials
or application configuration.

From the repository root:

```powershell
dotnet run --project tools/Receipts.JevProductionParity -- tools/Receipts.JevProductionParity/fixtures/synthetic-fruit.json
dotnet test tests/Infrastructure.Tests/Infrastructure.Tests.csproj --filter FullyQualifiedName~JevProductionParityTests
```

Build/restore may access package feeds. After building, run the tool DLL directly
to avoid build/restore and to obtain clean machine-readable stdout:

```powershell
dotnet tools/Receipts.JevProductionParity/bin/Debug/net10.0/Receipts.JevProductionParity.dll tools/Receipts.JevProductionParity/fixtures/synthetic-fruit.json
```

Supply synthetic or independently sanitized descriptions, taxonomy labels, and
judgments. The tool does not sanitize or certify input. Its output includes the
exact serialized request, so handle it with the same care as the fixture.

The fixture contains `id`, production `request`, and a typed production hierarchy
`judgment`. The checked-in example is entirely synthetic. Preserve taxonomy order:
production branch question names depend on it. A judgment must cover every root
and branch Choice, including branches the final path does not visit. Invalid
judgments remain production `InvalidResponse` results; invalid requests and
oversized payloads exit with code 2. Valid execution exits 0 even when the policy
rejects a judgment or abstains, since those are recorded evaluation outcomes.

`questionSha256` hashes the exact production UTF-8 request bytes, including state,
question order, and options. `decisionSha256` hashes the compact camelCase JSON of
the entire production decision, including provenance and usage. Both are lowercase
SHA-256 hex and exclude fixture id, filenames, timestamps, and environment values.
The fingerprints are stable for identical inputs and the same code/runtime; they
are not a canonical cross-language JSON standard. Compare records at a recorded
git revision and .NET SDK/runtime version. Model and policy version come directly
from production constants. No evaluation accuracy is implied by matching hashes.

This adapter covers question serialization and typed judgment policy only. It
does not validate raw provider-response parsing, authorization, concurrency,
credential lifecycle, transport errors, end-to-end orchestration, or model quality.
Those remain separate provider/integration tests. It cannot send inference calls.

## Offline paired packet runner

`offline-plan` verifies the frozen private packet hashes and six production source
snapshots against the supplied checkout, requires exactly 24 cases sharing one
ordered taxonomy, and writes exact baseline payloads to a new private file.
It does not read annotation labels or evaluate cases. It hashes the frozen
evidence-only annotation packet solely to verify packet integrity.

```powershell
dotnet run --project tools/Receipts.JevProductionParity -- offline-plan <private-packet-directory> <repository-root> <new-private-plan.json>
dotnet run --project tools/Receipts.JevProductionParity -- offline-replay <private-cases.json> <private-exchanges.json> <new-private-replay.json>
dotnet test tests/Infrastructure.Tests/Infrastructure.Tests.csproj --filter FullyQualifiedName~OfflinePairedEvaluationTests
```

Cases have `id` and production `request` fields. Exchanges are a sequential JSON
array of `{ "body": "<exact raw HTTP response text>", "statusCode": 200,
"requestSha256": "<lowercase SHA256 of exact request bytes>" }` objects.
The CLI requires every exchange to have a request fingerprint. Replay itself
does not verify a real cohort's freeze; use the validated plan and independently
verify input provenance before interpreting a real replay. Replay accepts 1–24
cases to support synthetic fixtures and preserves every supplied case in its
denominator. No real holdout responses have been collected by this work.

Replay invokes the actual `JevCategoryAssistanceSelectedProvider.SuggestAsync`
and `VerifySelectedAsync` with a fixed synthetic binding and placeholder key.
Its `HttpClient` has an in-memory handler with no network transport. Environment
credentials, application configuration, database and real connection bindings
are never loaded. Production payload serialization, raw Choice validation,
hierarchy reduction and the bound selected-path parser all execute unchanged.
This verifies offline production semantics; real authorization revocation and
HTTP networking still need separate integration evidence.

The baseline is shared between arms. Valid global abstentions skip the second
exchange. Assignments require the inherited production global fit gate 0.7;
treatment retains the same full path at selected support >=0.7, including useful
parent stops. Missing or request-mismatched exchanges are `unknown`; invalid or
HTTP error responses are `failed`. Either stops processing, preserves the suffix
as `unattempted`, and retains the attempt reservation. No retries, response
repair, real write, labeling, scoring or acceptance conclusion occurs. A completed
artifact exits 0 even when replay records a stopped run; inspect `stopped` and
outcome statuses. Input/artifact errors exit 2 without echoing private contents.

Replay simulates the frozen reservation policy: at most 48 attempts, USD
0.002752512 each, USD 0.132120576 maximum new reservation, plus USD 4.75 prior
carry = USD 4.882120576 combined ceiling reservation. This is offline accounting,
not a live ledger or account balance check. Pricing/context/free-output and actual
available credit remain unverified; later dispatch requires explicit authorization
and those checks. There is no live dispatch command. Request payloads and outcome
identifiers are written only to the exclusive-created output; keep all real
input/output files outside git. Run the DLL directly after build to avoid restore.
