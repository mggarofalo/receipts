# Jev evaluation and production parity

Offline and opt-in live evaluation of the production Jev request. The tool references
the shipped Infrastructure/Application assemblies and builds every request with the
production builder (`Infrastructure.Jev.JevTwoStageQuestions`), so evaluation and
production send the same bytes. There is no copied question wording or decision
policy.

Round 3 (`owner-labels-v1`, the paired baseline/selected-path runner and the single
fixture mode) was bound to the retired `classification-questions-v6` policy and has
been removed; its frozen journals and reports remain the record of that round.

## Opt-in sequential live controller

`live-run` is an explicit opt-in transport for round-4 packets; preparing/building/testing it sends no
requests. It uses the same replay to determine each exact next payload, then posts those bytes to the pinned `/v1/systemone` endpoint with Bearer
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
exchanges and whether its identity matches), and stage-1 request count, byte
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
Complete successful prefixes can resume, preserving the original ordering. Do not resume failed/unknown holdouts for
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

The round-4 request shape (`two-stage-v1` in the packet) is now production's
`classification-questions-v7`. `TwoStageQuestions.cs` only adapts a packet to the
production request; all wording lives in `src/Infrastructure/Jev/JevTwoStageQuestions.cs`.
An evaluation asks stage 2 for every case, where production skips it after a stage-1
abstention. A packet holds `policy.private.json`, `cases.private.json`
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
dotnet <dll> offline-production-v4 <packet-directory> <exchanges.json> <repository-root> <new-private-report.json>
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

**Production parity.** `offline-production-v4` rebuilds every recorded request with the
production builder and checks it against the recorded request hash, then replays the
recorded responses through the production parser, `CategorySuggestionPolicy` and
`CategoryAutoWritePolicy` at the default thresholds. The private report counts matched
request bytes, stage-2 requests production would send, abstentions, suggestions, and
automatic root and leaf writes (with leaf writes off, as configured, and on, for
comparison). It sends nothing and reads no configuration or credential.

## Catalog-matching packets (`catalog-matching-v1`, RECEIPTS-1091)

The catalog evaluation uses the same journal, approval, credential and transport safeguards around production's
`catalog-matching-questions-v1` ([spec](../../docs/jev-catalog-matching.md)). Every request is built by
`JevCatalogMatchingQuestions`, parsed by its parsers and decided by `CatalogMatchingPolicy`; the tool holds no wording or
decision logic. An evaluation asks stage 2 (Q3) about the stage-1 top choice for every case, where production asks it
only when it can change the outcome. A case with an empty shortlist is decided without a request.

A packet holds `policy.private.json` and `cases.private.json`, both bound by `freeze.private.json`. Each case has an
`id`, a `stratum` (`proxy`, `not-in-shortlist`, `hard-negative` or `injection`), the sent `description`, `store` and
`candidates`, and local-only labels. Real sources carry `leaveOneOut: true`, which removes their own description from
every candidate's approved examples before the request is built; synthetic examples are constructed evidence and are
kept.

```bash
# Real strata, read-only, into one private file (receipt text: never commit it).
RECEIPTS_RECALL_CONNECTION="Host=...;..." RECEIPTS_JEV_COHORT_OUTPUT=<private>/export.private.json \
  dotnet test tests/Infrastructure.IntegrationTests --filter Prerequisite=CatalogJevCohort
dotnet <dll> catalog-build <export> docs/normalization-evaluation/catalog-jev-1091.synthetic.json \
  docs/normalization-evaluation/catalog.v2.json <repository-root> <new-private-root> <source-commit>
dotnet <dll> catalog-plan <packet> <repository-root> <new-private-plan.json>
dotnet <dll> live-run <packet> <repository-root> <approval.json> <packet>/live.journal.jsonl <new-output.json> --dry-run --api-user-secrets
dotnet <dll> live-run <packet> <repository-root> <approval.json> <packet>/live.journal.jsonl <new-output.json> --send-authorized --api-user-secrets
dotnet <dll> catalog-parity <packet> <repository-root> <live-output.json> <new-private-parity.json>
dotnet <dll> catalog-score <packet> <repository-root> <new-private-score.json>
```

`catalog-build` writes a `smoke` packet (the preregistered 10 cases) and a `full` packet. The full packet's prior carry
is the smoke packet's whole reservation, so the two together stay within one approval. A catalog live run also appends
each response's latency to `latency.private.jsonl` in the packet. The journal format is shared with round 4 and is
unchanged. `catalog-score` writes an `aggregate` (counts, rates, calibration, gates and an exploratory threshold sweep,
with no text) and private per-case rows. The 2026-11-01 results are in
[catalog-jev-1091.md](../../docs/normalization-evaluation/catalog-jev-1091.md).
