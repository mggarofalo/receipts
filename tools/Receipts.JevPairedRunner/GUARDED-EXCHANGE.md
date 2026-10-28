# Offline guarded exchange seam (not a live runner)

This packet deliberately stops before a live transport. It does not read an API
key, discover proxies, open an HTTP connection, register a production service, or
send private data. Maximum paid/network calls is zero. It supplies reviewable
pieces for a future runner, not a completed paid campaign implementation.

The paired project now has an offline executable. With a frozen build, invoke:

```powershell
dotnet <absolute-path-to-Receipts.JevPairedRunner.dll> prepare <absolute-input.json> <absolute-replies.json> <absolute-output.json>
dotnet <absolute-path-to-Receipts.JevPairedRunner.dll> replay <absolute-input.json> <absolute-replies.json> <absolute-output.json>
```

`input` is the existing `PairedInput` JSON contract. `replies` is the existing
`ReplayReplies` contract; initial preparation uses
`{"production":"","shortlist":[],"verification":null}`. Preparation exports
the exact production and exhaustive shortlist payload strings. Supply the full
ordered recorded shortlist responses to prepare the actual candidate verifier.
Malformed/incomplete nonempty shortlists fail preparation. A valid shortlist may
abstain and omit verification. Replay invokes the existing actual provider and
candidate parsers/reducers, with recorded replies only; its result explicitly
contains failure/abstention fields. Exit zero means offline computation completed,
not a successful model answer. Output is CreateNew, an absolute path outside git
checkouts; caller must provision private directory permissions. No payload or
exception text is printed. Input/output files are bounded private evidence.

`scripts/offline-evaluation/guarded_exchange.py` is a Python API with no CLI or
live mode. Feed each exported UTF-8 payload to `exchange_recorded`, along with a
`RecordedTransport` containing one response, a Windows `CampaignLedger`, an
explicit new private output directory outside a checkout, and two nonempty hash
groups (`source` and `executable`, each mapping absolute file paths to expected
SHA-256). It verifies the supplied artifacts before reservation, durably reserves
before consuming the recorded response, writes exact request/response bytes and
a hash/usage receipt, then records success. Pass retained replies to the paired
executable afterward for actual classification decisions. Its strict envelope
validator is only a wire check, not a replacement for either actual reducer.

The ledger starts from main commit 23e80850, with added absolute-path and
reparse-ancestry validation before opening journal or lock files. Reuse the same
journal across every process/restart; do not create new
journals to evade campaign totals. One cooperative Windows local-fixed-disk owner
holds the lock throughout exchanges. Every reservation retains 65,536 tokens at
the ledger's stated pricing assumption, including successful exchanges. Any
exception after reservation leaves an unresolved outcome; further exchanges stop
even after restart. There is no automatic repair, retry, release or resume of an
uncertain attempt. A synthetic child-process crash test verifies this behavior.

A nonblocking process-wide exchange owner covers artifact/path checks, unresolved
outcome inspection, reservation, response consumption, persistence and outcome
recording. Concurrent threads fail immediately rather than waiting or retrying;
the journal's lifetime OS lock excludes other processes. A deterministic Windows
interleaving test pauses after the first unresolved-outcome snapshot, verifies a
second exchange cannot reserve, then verifies first-exchange failure blocks later
exchanges. Python and C# reject symlink/reparse ancestry component by component
before private path access; actual Windows directory-junction regressions cover
input, output, artifact and journal paths. Private directory ancestry must remain
stable: these checks do not claim protection against malicious concurrent path
replacement by another process.

There is no documented TypeSafe tokenizer/counter used here. Byte caps are not
token caps. Input/output token limits remain unknown. Null, malformed, negative,
boolean or reported usage over 65,536 stops the seam and retains the reservation.
The reservation is an accounting assumption; it does not guarantee that a local
$5 policy bounds the provider bill. No observed billing or model-quality evidence
is produced by synthetic replay. Hash groups identify explicitly supplied files;
they do not establish a reproducible build, dependency closure, trusted provenance,
or immunity to concurrent edits. Freeze and independently review the complete
source/executable dependency closure before any future live execution.

Remaining live integration work: a reviewed sequential controller linking both
arms and dynamic verification; durable stage/case bindings across restarts;
per-response actual parser validation before another send; complete artifact
attestation; sanitized transport/context-error classification; an explicit external
environment key; fixed `https://api.typesafe.ai/v1/systemone`, pinned
`jev-1.14.0`, no redirects, proxy discovery or retries; and reserve-before-every-send
enforcement. Provider context errors must remain failures. This seam intentionally
offers no live switch that could bypass those missing requirements.

Focused synthetic verification:

```powershell
python -m unittest discover -s scripts/offline-evaluation -v
dotnet test tests/Infrastructure.Tests --filter FullyQualifiedName~PairedReplayTests
```


## Persistent offline stage binding (bounded subset)

`scripts/offline-evaluation/stage_campaign.py` adds a synthetic-only Python API.
`StageCampaign(ledger, plan, artifacts=..., private_root=...)` uses the SAME
`CampaignLedger` journal/lock for immutable binding, starts, reservation/outcomes
and completions. The plan object is exactly:

```json
{"case":"invented-case","plan_sha256":"<paired plan SHA-256>",
 "production":"<exact exported payload string>",
 "shortlist":["<exact batch 0 payload>","<exact batch 1 payload>"]}
```

This journal now retains exact plan payload strings, so it must have the same
private access protections as request/response artifacts. The ledger byte cap
remains 1 MiB; oversized evidence fails closed. Labels/gold answers never belong
in the plan. Constructor/reopen verifies the same case, plan, source/executable
hashes, private evidence location and every completed request/response/receipt.
An optional `dependency` hash group is also retained and verified.

Use `exchange('baseline', RecordedTransport(reply))`, then
`exchange('shortlist:0', ...)` through EVERY batch in original order. After all
those completions, `bind_verifier(exact_utf8_payload, verification_sha256)` binds
the dynamic export to the ordered response hashes, or `bind_verifier(None, None)`
records explicit abstention. Only then can `exchange('verifier', ...)` run.
`complete` requires the verifier completion or explicit abstention. No stage may
be replayed, skipped or reordered. A durable start is written BEFORE reservation;
any interrupted stage stops permanently, including a crash AFTER an exchange's
success outcome but BEFORE its stage completion. Inspect retained evidence;
there is no recovery/retry/release API. The process-wide nonblocking exchange
owner covers the entire transition and prevents concurrent reservations.

This subset binds ONE case per existing campaign journal. Multi-case continuation
under the SAME aggregate campaign budget is still missing. Do not create new
journals to evade aggregate limits. The old raw `exchange_recorded` API remains
an offline primitive; it does not implement stage ordering. StageCampaign rejects
attempts interleaved through that API. There is no live entry point.

## Executed offline artifact identity

`frozen_runner.FrozenRunner(roots, artifacts, host=..., assembly=...)` accepts
three complete, explicitly reviewed inventories: `source`, `executable` (publish
output), and `dependency` (runtime). Each `roots` value is an absolute frozen
directory; the matching artifact group maps EVERY file's absolute path to its
SHA-256. Missing/extra files, hash drift, and reparse ancestry fail validation.
The explicit `dotnet.exe` must belong to the runtime inventory and the explicit
`Receipts.JevPairedRunner.dll` must belong to the publish inventory.
`run('prepare'|'replay', input_path, replies_path, new_output_path)` launches those
absolute files without a shell, PATH lookup, build/restore or inherited environment.
The bridge discards child stdout/stderr, checks exit status, and verifies inventories
and input hashes again after exit. Output is bounded, private and CreateNew through
the existing C# command. Tests fake this subprocess; they do not claim a successful
real .NET invocation. No revision string substitutes for file hashing.

These inventories establish identity of supplied files only. An independent build
review must establish their completeness: source snapshot, transitive project and
package inputs, publish dependencies and runtime/native dependency closure. The
bridge cannot prove provenance, hermetic loading, reproducibility or defeat malicious
concurrent file replacement. Its Python interpreter/modules and OS remain part of
the trusted environment. It cannot make arbitrary supplied binaries safe.

## Exact remaining route to live integration

1. Add multi-case state transitions to the SAME campaign journal; preserve the
   original aggregate policy and historical uncertain carry/reservations.
2. Couple StageCampaign initialization to the reviewed FrozenRunner's actual
   `prepare` output, and couple verifier binding to its actual `prepare` output
   over the exact retained ordered shortlist responses. Currently those APIs are
   separate: supplied verifier hashes/payloads are bound, not authenticated as
   actual reducer output. Explicit abstention is likewise caller supplied.
3. Add actual production/shortlist/verifier parser validation BEFORE another
   exchange can advance, including sanitized context-error classification. The
   current stage completion proves the strict wire envelope only. Preserve parser
   failure/abstention; never turn provider context errors into successful answers.
4. Independently review/freeze source and full executable/dependency closure;
   exercise the real offline executable bridge end to end, including crash,
   dynamic verifier, malformed answer, inventory drift and restart tests.
5. Implement a separate reviewed transport using the already authorized campaign
   budget and data scope. Keep an explicit externally supplied key, fixed
   `https://api.typesafe.ai/v1/systemone`, pinned `jev-1.14.0`, and no redirects,
   proxy discovery or retries. Preserve reserve-before-every-send and stop on
   any uncertain outcome. There is no new permission gate in these offline APIs;
   they expose no credentials, HTTP client or live switch.
6. Retain private exact requests/responses and both actual decisions; join held-out
   labels only afterward. Check actual billing/usage against reservation assumptions
   before claiming budget compliance or model-quality results.

Synthetic evidence added here: stage restart/order/replay, immutable plan, retained
response drift, malformed response, early/duplicate verifier binding, abstention,
thread interleaving, and real child-process crash between exchange success and stage
completion; inventory membership/hash drift and exact subprocess arguments. Run
`python -m unittest discover -s scripts/offline-evaluation -v` (40 tests at staging).

Journal and lock checkout ancestry is rejected before opening either file. Frozen
artifact hashes are verified again after response persistence and before success
outcome/stage completion. Payload snapshots are immutable tuples derived from the
canonical private plan; public plan/binding properties return detached copies.


## Coupled actual-parser offline stages

`paired_stage_campaign.PairedStageCampaign(ledger, frozen_runner, input_path,
case=..., private_root=...)` is the single-case executable-backed controller.
It accepts no caller plan, verifier payload, or abstention decision. Construction
runs actual frozen `prepare`; `bind_verifier()` runs it again over the exact
retained ordered shortlist. Every exchange runs the actual C# `validate` command
before recording exchange success or stage completion. This command uses the real
production provider with a sealed recorded handler, the same exhaustive shortlist
parser (accepting an ordered prefix while staging), and the actual candidate
parser. Valid abstention remains distinct from parser failure. A parser failure,
crash or inventory/input drift leaves the started stage terminal; reservations
already made remain unresolved and charged. Reservation still precedes response
consumption. The distinct journal format also prevents reopening this binding
through the lower-level caller-plan StageCampaign API.

The journal binds exact input bytes/path, host/assembly paths, roots and all three
inventories. Reopen re-exports the same plan and verifies completed evidence. Each
prepare/validate invocation retains new private replies/output files, outside git,
with no overwrite. This authenticates derivation relative to the explicitly
supplied executable; independent closure/provenance review remains required.
Caller-provided binaries and the interpreter/OS remain trusted. No malicious
concurrent path/file replacement protection is claimed.

`test_paired_stage_campaign.py` requires already restored local assets and publishes
with `--no-restore` (never implicit NuGet access). It snapshots tracked
source, copies an explicit .NET 10 host/runtime, hashes complete frozen inventory
directories, and executes real child processes with no inherited environment.
Synthetic checks cover dynamic verification, abstention, restart, malformed
parser input, input drift before reservation, and terminal malformed exchange.
A child exits immediately after actual parser validation to exercise the unresolved
reservation crash gap. This is offline integration evidence only.

Earlier route-to-live items 2 and 3 are now supplied by this controller; the raw
StageCampaign API remains an offline lower-level primitive. Multi-case journal
continuation, live HTTP/credential transport, sanitized provider status/context
classification and independently reviewed frozen dependency closure remain out of
scope. No live switch was introduced and no paid/model-quality claim is made.
