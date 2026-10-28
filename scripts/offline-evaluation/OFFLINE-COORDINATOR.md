# Synthetic offline coordinator foundation

`OfflineCoordinator` consumes only `RecordedTransport` bytes, never network or
credentials. A named ledger reservation is fsynced before consuming a response.
The exact response and its request and stage binding are written to a new partial
file, flushed, then renamed to a complete file before the supplied parser runs.
On restart a complete response is checked and reparsed without another send; a
missing or partial response remains unknown and cannot be sent again. A failed
parse can be retried from the same response. Other cases may proceed under the
same ledger allowance; an unresolved stage blocks later work in its own case.
The caller must use one stable private directory and ledger, and preserve them.
The journal and directory are local evidence, not protection against rollback,
manual alteration, or hardware failure; a lost rename leaves an unknown stage.

This primitive does not schedule phases, enforce their order, generate requests,
validate the real Jev response, derive a shortlist or abstention, run either C#
arm, record source/build/runtime/policy provenance, or export predictions. A
caller-provided parser is not evidence that a real C# parser ran. It is not an
API client or authorization for live calls. The same-case guard only prevents
progress past an unresolved earlier reservation; it does not prove dependencies.
