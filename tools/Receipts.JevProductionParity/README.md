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

## Packet policy

Every packet carries a frozen `policy.private.json`. Its SHA256 (and that of
`cases.private.json`) must appear in `freeze.private.json` `filesSha256`. All
fields are required; extra fields are ignored:

```json
{"packetKind":"owner-labels-v1","cases":12,"maximumAttempts":24,"reservationNanodollarsPerAttempt":2752512,"conservativePriorCarryNanodollars":4873863040,"cumulativeCeilingNanodollars":5000000000,"baselineGate":0.7,"autoWriteGlobalGate":0.9,"autoWriteSelectedGate":0.8,"liveAuthorized":false}
```

Rules: 1–48 cases; `maximumAttempts` = 2 × cases (one baseline plus one
conditional selected-path attempt each); per-attempt reservation fixed at
65,536 input tokens × USD 0.042/million = 2,752,512 nanodollars; carry >= 0;
ceiling <= USD 5; carry + maximum reservation <= ceiling; `baselineGate` 0.7;
auto-write gates finite in [0.7, 1]; `liveAuthorized` false (dispatch is
authorized only by a separate approval file). `freeze.private.json` records a
40-character lowercase `sourceCommit`, but the real pin is the six
`production-source/` snapshots, which must be byte-identical to the checkout.
`filesSha256` paths must stay inside the packet.

The auto-write gates are scoring inputs. Replay applies only the production
gates (global fit 0.7, selected-path support 0.7); treatment output carries
`selectedPathSupport` so a scorer can apply the stricter policy gates.

## Offline paired packet runner

`offline-plan` verifies the packet freeze, policy and source snapshots against
the supplied checkout, requires exactly `cases` cases sharing one ordered
taxonomy, and writes the policy numbers, gates and exact baseline payloads to a
new private file. It does not read annotation labels or evaluate cases.

Every offline command takes the repository root and rejects an output path
inside it, the same guard `live-run` applies. Outputs and the live journal are
created owner-only (`0600` on Unix) and never replace an existing file.

```powershell
dotnet run --project tools/Receipts.JevProductionParity -- offline-plan <private-packet-directory> <repository-root> <new-private-plan.json>
dotnet run --project tools/Receipts.JevProductionParity -- offline-replay <policy.private.json> <private-cases.json> <private-exchanges.json> <repository-root> <new-private-replay.json>
dotnet test tests/Infrastructure.Tests/Infrastructure.Tests.csproj --filter FullyQualifiedName~Jev
```

Cases have `id` and production `request` fields. Exchanges are a sequential JSON
array of `{ "body": "<exact raw HTTP response text>", "statusCode": 200,
"requestSha256": "<lowercase SHA256 of exact request bytes>" }` objects.
The CLI requires every exchange to have a request fingerprint. Replay validates
the policy file but not a packet freeze; use the validated plan and independently
verify input provenance before interpreting a real replay. Replay accepts 1 to
`cases` cases to support synthetic fixtures and preserves every supplied case in
its denominator.

Replay invokes the actual `JevCategoryAssistanceSelectedProvider.SuggestAsync`
and `VerifySelectedAsync` with a fixed synthetic binding and placeholder key.
Its `HttpClient` has an in-memory handler with no network transport. Environment
credentials, application configuration, database and real connection bindings
are never loaded. Production payload serialization, raw Choice validation,
hierarchy reduction and the bound selected-path parser all execute unchanged.
This verifies offline production semantics; real authorization revocation and
HTTP networking still need separate integration evidence.

The baseline is shared between arms. Valid global abstentions skip the second
exchange. Treatment is the baseline suggestion with the same full path kept at
selected support >=0.7 (including useful parent stops), otherwise category
cleared with `uncertain-selected-path`. Missing or request-mismatched exchanges
are `unknown`; invalid or HTTP error responses are `failed`. Either stops
processing, preserves the suffix as `unattempted`, and retains the attempt
reservation. No retries, response repair, real write, labeling, scoring or
acceptance conclusion occurs. A completed artifact exits 0 even when replay
records a stopped run; inspect `stopped` and outcome statuses. Input/artifact
errors exit 2 without echoing private contents.

Replay simulates the policy reservation: at most `maximumAttempts` attempts at
USD 0.002752512 each, plus the prior carry, never above the ceiling. This is
offline accounting, not a live ledger or account balance check. Actual remaining
provider credit is unknown. The offline commands never dispatch. Request
payloads and outcome identifiers are written only to the exclusive-created
output; keep all real input/output files outside git. Run the DLL directly after
build to avoid restore.

## Opt-in sequential live controller

`live-run` is an explicit opt-in transport; preparing/building/testing it sends no
requests. It uses the same production replay parser to determine each exact next
payload, then posts those bytes to the pinned `/v1/systemone` endpoint with Bearer
authentication and JSON content type. There are no redirects, application retries,
long-context headers, fallback calls or production writes. Never print the key or
private artifacts.

Credential: `TYPESAFE_API_KEY` from the process environment wins; a set but empty
value is missing. Only with `--api-user-secrets`, and only when the variable is
unset, the key is read from the API project's .NET User Secrets: the single
`<UserSecretsId>` in `src/Presentation/API/*.csproj`, then
`~/.microsoft/usersecrets/<id>/secrets.json` (Windows:
`%APPDATA%/Microsoft/UserSecrets/<id>/secrets.json`), top-level
`TYPESAFE_API_KEY` string. Paths, file contents and parse errors are never
reported. The key is re-resolved before every send and must be unchanged.

Before dispatch, verify current pricing/context and executable identity. The authorized
operator supplies an external private approval JSON with these fields (values below
are a schema example, not approval):

```json
{"freezeSha256":"<sha256 of freeze.private.json>","expiresUtc":"<UTC expiry>","authorized":true,"pricingAndContextVerified":true,"priorCarryNanodollars":4873863040,"authorizedBudgetNanodollars":5000000000}
```

`priorCarryNanodollars` must equal the policy carry and
`authorizedBudgetNanodollars` the policy ceiling (the user's cumulative spending
ceiling); carry plus the maximum new reservation must fit within it.
`pricingAndContextVerified` attests only to Jev input USD 0.042/million, free
output and default 64k input context without a long-context header. Prior
allocation is not future credit required. No exact prior bill or remaining
account balance is asserted: local journals are incomplete billing evidence, and
no provider balance API or account UI is available in this workflow. Budget
authorization does not prove sufficient remaining credit. Insufficient-credit
responses stop the run, retain the attempt reservation, and cannot be retried on
resume. Stop before dispatch if known additional spend invalidates the carry.
Never manufacture a balance attestation. The fixed 65,536-token reservation is
deliberately above a 64,000-token limit; request byte size does not estimate
tokens. Any reported input usage above 65,536 stops further sends.

After building, use the DLL directly so build logs cannot obscure the controller:

```powershell
dotnet tools/Receipts.JevProductionParity/bin/Debug/net10.0/Receipts.JevProductionParity.dll live-run <packet-directory> <repository-root> <private-approval.json> <packet-directory>/live.journal.jsonl <new-private-output.json> --dry-run [--api-user-secrets]
dotnet tools/Receipts.JevProductionParity/bin/Debug/net10.0/Receipts.JevProductionParity.dll live-run <packet-directory> <repository-root> <private-approval.json> <packet-directory>/live.journal.jsonl <new-private-output.json> --send-authorized [--api-user-secrets]
```

Exactly one of `--dry-run` or `--send-authorized` is required. `--dry-run`
performs every check that needs no network and writes a report
(`mode: "dry-run-no-dispatch"`) to the new output: policy numbers and gates,
`credentialPresent` (boolean only), approval status (`valid`, `missing`,
`unreadable`, `not-authorized`, `pricing-unverified`, `freeze-mismatch`,
`expired` or `budget-mismatch`), journal status (`absent`, `empty`, `resumable`,
`outstanding-intent`, `invalid` or `locked-or-unreadable`, with completed
exchanges and whether its identity matches), and baseline request count, byte
sizes and SHA256s. It never contains request bodies, descriptions or the key,
never creates a transport and never creates or modifies the journal. A missing
approval or credential is reported, not fatal; packet, path or output errors exit 2.

The CLI requires all private paths outside the supplied checkout and exactly the
single named journal within the packet. Protect that private directory with the
existing user-only access controls. Never delete, replace, copy to a new campaign,
or manually repair the journal to obtain another send. Freeze (which binds the
policy), cases, approval, tool/Infrastructure/Application assembly hashes, and a
private one-way credential fingerprint bind its identity across restarts. A
replacement key blocks resume even if it might belong to the same account; keys
are never persisted. Expiry, revocation, credential change or any approval edit
blocks dispatch. A changed approval cannot silently renew an existing journal.

A linked ten-second cancellation deadline covers request dispatch and the entire response body read. A stalled body retains its outstanding intent and cannot be retried on resume. The journal holds an exclusive process lock. Each intent and its full reservation
are flushed to disk before transport; every complete response is flushed before
the next attempt. Responses retain provider usage privately. All reservations
remain charged, including failed/unknown attempts, up to the policy maximum.
The controller reuses saved responses through the production parsers on resume.
A missing response, partial journal, parse/HTTP failure, request drift (on any
saved exchange, including the last) or ambiguous send stops; an outstanding intent is never resent automatically.
Complete successful prefixes can resume, preserving the original ordering and
conditional selected-path calls. Do not resume failed/unknown holdouts for
tuning. Disk corruption, deletion, manual edits and separate copies of a packet
require operator investigation.

Exit 0 means all cases completed (possibly valid abstentions) or a dry-run
report was written, 3 means a parsed stopped run, and 2 means another
failure/ambiguous state. Preserve the private journal even if no final report
was created. No exception text, receipt data, response body or key is emitted to
the console. Tests inject synthetic transport, environments and secrets roots and
make no inference calls. Preparation adds no account/authorization binding to
the production application and does not authorize automatic classification.

## Round-4 two-stage packets (`owner-labels-v4`)

An evaluation-only request shape, `two-stage-v1`, built in this tool
(`TwoStageQuestions.cs` holds all wording). Production code is not called or
changed. A packet holds `policy.private.json`, `cases.private.json`
(`id`, `description`, nullable `store`) and `taxonomy.private.json` (item roots
only, placeholder leaves removed), all three bound by `freeze.private.json`.
`offline-plan` and `live-run` recognise the packet kind; there are no production
source snapshots to pin.

- **Stage 1:** state `purchase.receiptText` and `purchase.store`; one Choice
  over the item roots keyed by name, and a "does one of these categories
  describe it?" Noul.
- **Stage 2:** one Choice over the leaves of the stage-1 top three roots, keyed
  `Root / Leaf`, in taxonomy order. Back-off to the root is left to the scorer.

```bash
dotnet <dll> offline-plan <packet-directory> <repository-root> <new-private-plan.json>
dotnet <dll> offline-replay-v4 <packet-directory> <exchanges.json> <repository-root> <new-private-replay.json>
dotnet <dll> live-run <packet-directory> <repository-root> <approval.json> <packet-directory>/live.journal.jsonl <new-output.json> --dry-run
```

Policy (all fields required; extra fields ignored):

```json
{"packetKind":"owner-labels-v4","model":"jev-1.14.0","questionPolicy":"two-stage-v1","cases":60,"maximumAttempts":120,"approvedMaximumAttempts":120,"approvedReservationCapNanodollars":350000000,"reservationBasis":"utf8-request-bytes","inputNanodollarsPerToken":42,"headroomInputTokens":65536,"conservativePriorCarryNanodollars":250000000,"cumulativeCeilingNanodollars":5000000000,"liveAuthorized":false}
```

**Reservation.** Each attempt reserves one input token per UTF-8 byte of its
exact request, at 42 nanodollars per token (USD 0.042 per million). Byte-level
tokenizers produce at most one token per byte, and every round-3 request used
0.41 to 0.57 tokens per byte, including the small single-question ones, so the
server's own framing did not push usage past the byte count. Because that is an
empirical bound, not a provider guarantee, the packet also holds back one
65,536-token attempt as headroom. A reported usage above an attempt's
reservation stops the run before the next send, so at most one attempt can
exceed its reservation, and a request over the provider's 64k context is
rejected. The packet's maximum reservation is every stage-1 request's bytes,
each case's largest possible stage-2 request, and the headroom. The largest
stage-2 request uses the three roots whose options take the most bytes: the
request size is a fixed part plus each chosen root's option bytes. The runner
refuses a packet whose maximum exceeds `approvedReservationCapNanodollars`, or
whose carry plus maximum exceeds the ceiling, and an approval must restate the
same carry and ceiling. Every other live safeguard (pinned model, durable intent
before each send, no retries, ten-second deadline, key bound to the journal) is
the shared journalled loop used by round 3.
