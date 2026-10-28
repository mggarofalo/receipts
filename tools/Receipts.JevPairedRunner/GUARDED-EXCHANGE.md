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
