# Category assistance persistence

Category assistance is a deployment-wide, opt-in service. Configuring credentials does not enable it. Any active Admin may change consent and limits; eligible saves come from authenticated receipt writers. Manual category choices and approved exact-source mappings take precedence.

The current foundation is dormant: the dispatch store and Application orchestrator are not registered as a running worker, and the transport inference gate remains closed. Runtime transport binding, scheduling, settings/read/accept APIs and capability-driven UI are separate required integration work.

## Dispatch evidence

Only trusted post-consent, no-choice save observations can enroll an attempt. Exact normalized store, description and optional item code are compared alongside the source hash; deduplication also includes taxonomy, provider/model, question policy and deployment generations. The provider receives the description and taxonomy, not receipt payment or account details.

Queued claims use bounded leases and deferral. Dispatch start atomically checks current consent, source and receipt/item revisions, assignment, mapping, taxonomy and connection state, then debits the deployment-wide UTC-day allowance. A dispatched attempt is never automatically resent after an uncertain outcome. Completed results remain suggestions and do not assign categories or approve mappings.

## Connection and restore fences

Assistance has its own persisted connection generation, independent of the ChatGPT normalization connection. The selected transport must bind to the captured generation and check it immediately before sending. Dispatch start is the authorization boundary; revocation may overlap an already-started request, whose stale result must be discarded at completion.

Portable import disables consent, rotates assistance restore and connection generations, invalidates outstanding work and records the transition in the import transaction. Failed import rolls the transition back. Daily usage survives portable restore. Operational settings, connection state, attempts and usage are excluded from portable archives; approved mappings and classification history remain portable.

Downgrading the dispatch schema refuses to erase existing attempts or daily usage. Raw PostgreSQL restores are supported through the controlled Compose procedure in `scripts/restore.cs`. It stops the app before reading or replacing the database, writes the current daily usage ledger to a separate CSV beside the dump, restores with checked gzip decompression and PostgreSQL error stopping, then reconciles before starting the app. Reconciliation uses the same lock order and transition as the portable fence: it disables consent, rotates settings, restore and connection generations, marks active attempts stale or indeterminate, clears their results and leases, and appends a settings audit. For each UTC day, it retains the larger of the captured and restored usage counts. The saved CSV must be protected and retained until recovery is verified.

On any failure before restart, including a failed app stop, dump, schema check, ledger read or reconciliation, the script exits without starting the app. If restart fails, it tries to stop the app again and reports whether that stop succeeded. If the restored image predates the assistance schema, apply migrations while the app remains stopped, then run `dotnet run scripts/restore.cs -- --reconcile <saved-ledger.csv>`. This path repeats the fence and usage merge before starting the app. A fresh disaster recovery with no surviving ledger may use `--reconcile` without a CSV after migrations; it disables consent and exhausts the current UTC day's allowance at 100. Usage history is then uncertain. Recover an authoritative out-of-image ledger or wait until its unknown days have passed before an Admin considers re-enabling assistance.

Direct `psql`, volume replacement and other restore paths bypass this procedure and cannot be inferred from database generations. Keep the app stopped and run the reconciliation command with the best surviving ledger before use. No automatic detection of arbitrary rollback is claimed.
