# Category assistance persistence

Category assistance is a deployment-wide, opt-in service. Configuring credentials does not enable it. Any active Admin may change consent and limits; eligible saves come from authenticated receipt writers. Manual category choices and approved exact-source mappings take precedence.

The current foundation is dormant: the dispatch store and Application orchestrator are not registered as a running worker, and the transport inference gate remains closed. Runtime transport binding, scheduling, settings/read/accept APIs and capability-driven UI are separate required integration work.

## Dispatch evidence

Only trusted post-consent, no-choice save observations can enroll an attempt. Exact normalized store, description and optional item code are compared alongside the source hash; deduplication also includes taxonomy, provider/model, question policy and deployment generations. The provider receives the description and taxonomy, not receipt payment or account details.

Queued claims use bounded leases and deferral. Dispatch start atomically checks current consent, source and receipt/item revisions, assignment, mapping, taxonomy and connection state, then debits the deployment-wide UTC-day allowance. A dispatched attempt is never automatically resent after an uncertain outcome. Completed results remain suggestions and do not assign categories or approve mappings.

## Connection and restore fences

Assistance has its own persisted connection generation, independent of the ChatGPT normalization connection. The selected transport must bind to the captured generation and check it immediately before sending. Dispatch start is the authorization boundary; revocation may overlap an already-started request, whose stale result must be discarded at completion.

Portable import disables consent, rotates assistance restore and connection generations, invalidates outstanding work and records the transition in the import transaction. Failed import rolls the transition back. Daily usage survives portable restore. Operational settings, connection state, attempts and usage are excluded from portable archives; approved mappings and classification history remain portable.

Downgrading the dispatch schema refuses to erase existing attempts or daily usage. A raw full-database disaster restore requires a separate out-of-image continuity procedure; database generation equality alone cannot detect an arbitrary rollback. That operator/runtime integration remains a release prerequisite.
