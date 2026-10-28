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
