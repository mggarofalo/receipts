# Explicit single-case live transport seam

`live_stage_campaign.LivePairedStageCampaign` adds a separate Python API. This
component has not sent a paid request and is not a completed campaign runner.
The existing offline classes remain offline. No production services register it.
Use only within the existing authorized real-data scope and aggregate budget;
this implementation adds no new user confirmation gate.

Construction has the same ledger, actual FrozenRunner, input, case and private
root as `PairedStageCampaign`, plus **required** `key_environment`,
`python_executable`, and `worker_path` arguments. The environment variable name
is explicit (for example `TYPESAFE_API_KEY`); the key is read only after durable
stage start and reservation, never searched from files or logged. The child gets
this named variable plus `SystemRoot` resolved through the Windows API, with no
inherited proxy, Python path or other secrets. Windows socket providers require
`SystemRoot`; its exact value is journal bound. It cannot be the credential name.

Live construction requires every executing evaluation Python source file and
the adjacent `live_https_worker.py` in the frozen **source** inventory, and the
current absolute Python executable in the frozen **dependency** inventory.
The exact interpreter/worker/name/endpoint/deadlines are journal bound. Thus
dispatch must launch the controller from the reviewed frozen source snapshot,
using the frozen Python interpreter (use `-B` to avoid inventory drift). Copy
and independently review its complete standard-library/native/TLS dependency
closure alongside the .NET runtime. Supplying hashes does not prove provenance,
complete hermetic loading, or safety against hostile concurrent file changes.
The caller must provision private directory ACLs and stable ancestry outside git.

Call `exchange('baseline')`, then every ordered `exchange('shortlist:N')`,
`bind_verifier()`, and, when required, `exchange('verifier')`. No transport
argument is accepted. The fixed endpoint is
`https://api.typesafe.ai/v1/systemone`, model `jev-1.14.0`. `http.client` does
not discover proxies, follow redirects or retry. TLS uses the interpreter's
default certificate validation. Each child performs one POST with a 20-second
socket timeout; the parent kills and reaps it after a 30-second total deadline,
including DNS/TLS/body delays. No next stage can send until the child finishes,
the response is retained privately, strict envelope/usage checks pass, and the
**actual C# production/shortlist/verifier parser** validates it. A valid
abstention remains distinct from malformed data.

Responses require HTTP 200, JSON content type, identity encoding, unambiguous
framing and at most 1 MiB. The request cap is 256 KiB plus the ledger's tighter
policy cap. Redirects, provider context errors, auth/rate errors, timeouts,
truncated or invalid bodies and null/unknown/excessive usage are terminal
failures. Error bodies and exception details are never printed. A durable stage
start and full 65,536-token reservation precede any network or key read. Every
exception retains the full unknown reservation and prevents another exchange,
including after restart. Successful reservations are also never refunded.

The internal child file is a transport implementation detail, **not** an
independent dispatch command; only the reserved controller may launch it.

Live provider docs checked 2026-10-28:
[HTTP API](https://docs.typesafe.ai/go/api.md),
[models and limits](https://docs.typesafe.ai/go/models.md). They document
64k/request and 32k/question context, and $0.042/M input tokens. There is no
verified local tokenizer here: byte limits do not establish token/context
compliance, and the 65,536-token reservation is an accounting assumption, not
proof of a provider bill cap. Do not claim observed billing or model quality.

`test_live_frozen_campaign.py` independently stages the full installed CPython
standard library (all source/resources excluding third-party site-packages and
bytecode caches) into a hashed ZIP, all interpreter-root files and the complete
DLLs tree including native TLS/OpenSSL files. A per-file SHA manifest verifies
every ZIP member and copied native file. A generated `._pth` disables ambient
registry/site/Python paths; the child asserts all imported modules resolve within
the frozen source/runtime roots, and creates a certificate-validating SSL context
without opening a socket. Windows system DLLs and root certificate stores remain
explicit external OS trust, not claimed as frozen artifacts.

This fixture uses public live construction, the real frozen C# `prepare` and
`validate` commands and real ledger; only `_send_reserved` is replaced. It covers
baseline/reopen/shortlist/dynamic verifier success and an envelope-valid UTF-8 BOM
response rejected by the actual C# parser, with terminal failure after reopen.

Remaining before campaign dispatch: independently review the campaign's actual
executable/source/runtime snapshot and add multi-case continuation and label
joining under the **same aggregate ledger**. Do not create replacement journals
to evade totals. This single-case controller does not implement a full cohort,
prediction export, billing reconciliation, or automatic recovery. No live call,
secret lookup or private SQLite access occurred during implementation/testing.

Synthetic transport tests:
`python -m unittest discover -s scripts/offline-evaluation -p test_live_transport.py -v`.
They fake HTTPS/subprocesses and test durable reservation/failure ordering with

the real Windows ledger. Run the separate frozen integration evidence with
`python -B -m unittest discover -s scripts/offline-evaluation -p test_live_frozen_campaign.py -v`.
It requires restored local .NET assets and never sends a provider request.

The self-contained campaign journal is capped at 2,097,152 bytes. Each full
canonical record, including chain metadata and its terminating newline, has a
separate byte cap: policy 2,048; bind 1,179,648; verifier 524,288; and start,
attempt, outcome and finish 512 each. Append and replay both enforce these caps,
known record/event kinds, one policy/bind/verifier, and at most the policy's
allowed count of each request record. Replay rejects noncanonical or malformed
records without repair. The exact payload/artifact binding stays in this journal.

Before a stage start or request reservation, capacity is checked for the whole
policy: policy + bind + verifier + four records per allowed request. At 96 calls
this requires at most 1,902,592 bytes. Insufficient capacity fails before that
start/reservation is written. The larger journal changes no call, spend, payload,
path, locking, fsync, poison, pending-stage or no-retry safeguards. This remains
one immutable case under the existing aggregate ledger; it adds no continuation,
reservation reset, refund, network action or credential access.


The separate `BatchedPairedStageCampaign` remains **offline only** and accepts
only recorded transport. It uses the actual frozen `prepare-batched`,
`validate-batched`, and `replay-batched` commands. After every ordered shortlist
response has succeeded, one durable `verifiers` record binds the complete ordered
vector (at most four path batches followed by one global coverage batch), its
actual plan hash and all prerequisite response hashes. Reopen re-derives that
entire vector from the frozen executable and exact shortlist responses. Legacy
`verifier` and batched `verifiers` are mutually exclusive and share the same
single-record allowance and **aggregate 524,288-byte cap**; five separate
524,288-byte records are not permitted. The total 2,097,152-byte limit and the
1,902,592-byte worst-case proof at 96 calls therefore remain unchanged. Overflow
fails before binding and before any verification request starts. Per-batch
payloads remain capped at 32,768 UTF-8 bytes. Canonical escaping and metadata
count toward the full aggregate record cap.

Stage names are `baseline`, ordered `shortlist:N`, ordered `verifier:N`, then
`global-coverage`. Actual prefix parser validation precedes each success, and
actual complete aggregation precedes final coverage success. `result` rejects
incomplete campaigns; explicit actual-reducer abstention has no verification
requests. Interrupted, malformed or failed stages remain terminal across reopen;
there is no retry, refund, automatic repair, multi-case
continuation, private-data discovery or activation. Run synthetic frozen
integration and capacity evidence with:
`python -B -m unittest discover -s scripts/offline-evaluation -p test_batched_stage_campaign.py -v`.

`live_batched_stage_campaign.LiveBatchedPairedStageCampaign` is the explicit
opt-in live counterpart. It uses the same required constructor arguments and
reviewed subprocess HTTPS transport as `LivePairedStageCampaign`, including
reservation before credential access or network, fixed host/model, stripped
environment, deadlines and no retries. Its frozen source inventory additionally
requires both batched controller modules. It uses the ordered batched stage names
above and actual frozen C# planning, prefix validation and complete aggregation.
The original live and recorded controllers keep their existing behavior.

The journal remains capped at 2 MiB, with at most four path batches followed by
one global coverage request. Byte-based splitting can require more than four
path batches even when the selected leaf count fits; that vector is rejected
before binding and before any verification request is reserved or sent. Aggregate
record overflow also fails before verification, without increasing journal caps.

`test_live_batched_frozen_campaign.py` uses only invented taxonomy data, isolated
frozen Python and real frozen C# with a fake transport send boundary. It exercises
successful multiple path batches plus final global coverage, public reopen,
malformed global replies, unknown usage and aggregate capacity rejection. It reads
no credentials and sends no provider request. Run it with
`python -B -m unittest discover -s scripts/offline-evaluation -p test_live_batched_frozen_campaign.py -v`.

## Safe terminal OS diagnostics

`safe_os_diagnostics.py` supplies fixed `Stage` and `Operation` identifiers and
`SafeOSError.diagnostic`, containing only `kind`, `stage`, `operation`, integer
`errno`/`winerror` (or null when unavailable). It never copies exception text,
filenames, response bodies, receipt contents, credentials, or exception class
names supplied by a caller. The live campaign annotates stage start, reservation,
directory creation, request/response/receipt open/write/flush/fsync/close, outcome,
finish, and child creation. Other OS faults receive the exchange/dispatch fallback.
The helper is required in the frozen source inventory like other executing modules.

An external launcher should catch `SafeOSError` before its generic exception
handler and serialize **only** the diagnostic dictionary into its terminal report:

```python
except SafeOSError as error:
    terminal_report['os_error'] = dict(error.diagnostic)
    # Continue the launcher's existing terminal, no-retry exit path.
```

For launcher-owned IO, use `with safe_os(Stage.RECEIPT, Operation.FSYNC):`
around the corresponding operation, selecting reviewed enum members rather than
deriving labels from input, paths, stage payloads, or exception text. Do not dump
exception attributes, `__context__`, tracebacks, or the entire terminal report to
public output. Persist any additional report with create-new semantics and do
not overwrite original terminal reports, journals, or retained artifacts. If
report persistence itself fails, the structured diagnostic can be emitted to the
launcher's existing safe terminal output; do not retry the exchange.

This repository does not contain the private pilot launcher or its v6 terminal
report writer. The public campaign is instrumented; the launcher must integrate
the catch above and include this helper in its reviewed frozen source snapshot
before new dispatch. Existing private evidence was neither read nor changed.
These identifiers diagnose a boundary; they do not establish whether a failed
write/fsync became durable. Full reservations, unresolved outcomes, poisoned
journals, pending stages, cap enforcement, and no-retry behavior remain in force.
No exception is converted to a successful or refunded outcome.

`test_safe_os_diagnostics.py` injects synthetic failures at every artifact
durability boundary and at post-validation receipt/outcome/finish boundaries.
It verifies numeric codes, exclusion of sensitive error strings/paths, primary
failure preservation, create-new protection, retained full reservations, and
terminal behavior after reopen. Runner and transport are fake in these fault
tests; they do not claim to execute the real C# parser or make paid calls.
