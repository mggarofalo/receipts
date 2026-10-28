# Offline campaign reservation foundation

`campaign_ledger.py` has **no HTTP/provider integration**, no credential access,
no automatic retries, no production registration and no request payload storage.
Tests use fictional bytes and temporary directories. This foundation does not
itself authorize or execute a dispatch, or prove provider billing totals.

One campaign uses one explicitly supplied, persistent journal on a Windows local
fixed disk. Create it once with an immutable `Policy`; every later worker and
restart opens that same existing journal. Keep journals outside the repository.
Use a context manager to release the nonblocking Windows byte-range lock.
Concurrent owners fail immediately without retries. The lock sidecar is permanent;
never unlink it. Unsupported platforms and network drives fail closed.

```python
from campaign_ledger import CampaignLedger, Policy

# Fictional offline example; 48 attempts, USD 0.20 ceiling, zero known carry.
policy = Policy("fictional-campaign", max_calls=48,
                budget_nanodollars=200_000_000, unknown_carry_nanodollars=0)
with CampaignLedger(explicit_path, create_policy=policy) as ledger:
    attempt_id = ledger.reserve_attempt(b'{"fictional":"example"}')
    # No dispatch exists. A future caller must consume this ID at most once.
    ledger.record_outcome(attempt_id, "unknown")
```

For a multicase coordinator, `reserve_attempt(payload, stage_id=[case, arm, phase, batch])`
atomically records that stable identity with the request hash and reservation. The
first three components are lowercase ASCII identifiers of at most 32 characters;
`batch` is an integer from 0 through 95. A stage identity can be reserved only
once, including across restarts and regardless of outcome. Different identities
share the same allowance. This is only an accounting primitive: it does not
persist responses, parse them, schedule cases, or authorize a transport. Callers
must establish those properties before using it to coordinate an experiment.

Before returning an attempt ID, the module appends and `fsync`s a reservation.
A crash after reservation consumes the slot even if nothing was dispatched.
Every success, error, unknown outcome, or missing outcome retains the entire
reservation forever. There is deliberately no usage reconciliation or release
API. A failed/uncertain write returns no permission and poisons the open instance.
After restart, valid persisted reservations remain charged; malformed, truncated,
oversized, duplicate-key or hash-chain-invalid journals fail closed without repair.

The immutable allowance is at most 96 attempted calls and USD 5 per journal.
Budget arithmetic uses integer nanodollars. Every attempt reserves **65,536 input
tokens at USD 0.042/million**, exactly USD 0.002752512, plus the campaign's explicit
unknown carry. The unknown carry must include earlier/other uncertain spending
within the same overall allowance before creating a campaign. Neither failed
requests nor retries are free: future integrations must disable transport/SDK
retries and create a fresh durable reservation for every attempted request.

These are conservative **local accounting limits**, conditional on the supplied
`jev-1.14.0` standard rate, 64k request context and free output assumptions. They
are not an exact provider-enforced dollar guarantee: undocumented billable input,
price changes, taxes, other campaigns, deleted/rolled-back journals, noncooperating
writers and API calls that bypass this module are outside this mechanism. Do not
reset a campaign to recover uncertain allowance. Share one journal or allocate
disjoint ceilings/carry explicitly within the overall user budget.

TypeSafe has no documented official token counter in the reviewed contract.
`max_payload_bytes` limits serialized **bytes only** (default 65,536; configurable
up to 262,144); it neither counts tokens nor proves fit within the request context
or the separate 32k question context. A later adapter must use complete serialized
request bytes, validate request/question limits and reverify current provider
pricing/context assumptions before dispatch. This foundation does not infer token
counts from characters or bytes and does not reinterpret a failed call as an answer.
Sources for later verification: [models](https://docs.typesafe.ai/go/models),
[HTTP API](https://docs.typesafe.ai/go/api). Live Markdown retrieval was unavailable
in this implementation session; these assumptions were supplied by the lead's
prior documentation audit, not independently reverified here.

Windows cooperative file locking protects simultaneous/restarted processes on a
local fixed disk. `fsync` requests OS durability; sudden hardware/storage failure,
external truncation to an earlier valid prefix, sidecar removal, aliases through
hard links or manual file replacement cannot be prevented by a local journal.
Keep a single canonical path and retain both files. This is append-only by module
behavior, not tamper-proof storage. Complete integration still requires an actual
dispatch wrapper that enforces single use and disallows bypass and automatic retry.

Run synthetic checks (Windows Python 3.11+):

```powershell
python -m unittest discover -s scripts/offline-evaluation -p test_campaign_ledger.py -v
```
