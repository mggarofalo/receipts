# Historical reclassification foundation (RECEIPTS-1051)

## Authenticated operation API

`POST /api/historical-reclassifications` captures a server-built preview. The request
contains only an operation ID, expected taxonomy revision, reason, and one to 25 exact
source groups with explicit target category choices. The server rejects a selected
group with no available items. No client item manifest or assignment history is accepted.

`GET /api/historical-reclassifications/{id}` returns durable progress counts. `GET
/api/historical-reclassifications/{id}/items?offset=0&limit=100` pages frozen item
evidence and apply/undo outcomes, with `nextOffset` for another page. `POST
/api/historical-reclassifications/{id}/apply` accepts `{ "limit": 100 }` and advances
at most 100 pending items. Repeat it while `pending` is nonzero. `POST
/api/historical-reclassifications/{id}/cancel` stops future chunks without reversing
committed rows. `POST /api/historical-reclassifications/{id}/undo` accepts a fresh
`taxonomyRevisionId`, immutable `reason`, and bounded `limit`; repeat with the same
intent while applied items still have pending undo outcomes.

All operations are scoped to the signed-in actor. A retry with the same operation ID
and preview intent returns persisted progress; a changed intent conflicts. Reasoned
rejections use `application/problem+json` with a readable `detail`.

Campaign group dispositions (`Later`, `LeaveAsIs`, or reopened) are actor-private
operational preferences. Each successful disposition request stores an immutable
operation ledger entry with its exact source, requested state, expected version, and
original response in the same transaction as the current disposition. An older
operation ID therefore replays its original result after later updates; reusing the
ID with a changed request conflicts. The dispositions and their ledger are included in
the public v14 portable taxonomy archive. Format 14 import transfers current
dispositions and their complete operation ledger under the restore write gate, rejecting
divergent destination decisions. Public export captures v14 automatic history, saved preferences and private dispositions,
including retry records. Public import accepts v14 and legacy v13 archives. The automatic background worker remains disabled. Neither is classification or verification evidence.

The foundation originally defined contracts and pure policies; persistence and the
authenticated operation API now implement the workflow described below. The service
does not run Jev or change purchase assignments until a user applies a captured preview.

## Explicit evidence and selection

A taxonomy edit changes future selectable categories; it never rewrites a historical
assignment. A migrated Grocery / Dairy assignment can be refined to Milk by a user
without a suggestion provider. Reclassification appends a new assignment; existing
history and recorded taxonomy paths remain immutable.

`HistoricalPreview` freezes the actor, operation ID, reason, captured time, taxonomy
head revision, selected exact group keys, and the exact item manifest. Each row freezes
its receipt ID, source item and receipt revisions, expected current assignment ID,
previous category, proposed category, and distinct apply/undo operation IDs. The
source tokens map to the existing item and receipt `NormalizationRevision` fields;
the exact group maps to receipt `Location`, item `Description`, and
`ReceiptItemCode`. String equality is ordinal: null and empty codes differ, as do
case and whitespace. No normalized descriptions or product links imply membership.

At most 25 groups can be selected per operation. This supports RECEIPTS-1053 selected
campaigns: pagination is presentation only. The server resolves only the explicitly
selected groups into the preview manifest; apply never reruns a broad search or
includes subsequently added items. All items from a selected group must be covered
by the preview's counts and downloadable/paged manifest, with explicit target choices.
The boundary must validate ownership, nonempty identities and reason, available
sources, and selectable targets against server-read evidence. A caller's arbitrary
snapshot is not proof of freshness. `ValidateSelection` enforces group membership,
unique item IDs, and distinct nonempty assignment operation IDs.

## Smallest persistence plan

Add an operation header and operation-item table, plus a migration in a later change.
The header key is `(Actor, OperationId)` and contains the immutable preview/request
hash, taxonomy revision and persisted state. Each item is unique by operation/item,
with a stable manifest ordinal, frozen evidence, previous/target category, two stable
assignment operation IDs, apply outcome/reason/applied assignment ID, and separate
undo outcome/reason/undo assignment ID. Store the preview hash from a canonical,
ordered serialization: same operation and same request replay the persisted result;
same operation and different request conflict. Never regenerate per-item operation
IDs after a restart. Retain operation records through backup/restore.

Use the existing classification authority,
`ClassificationService.AssignInTransactionAsync`, inside the same database transaction
as each item's durable outcome. Do not introduce another current-assignment writer.
Acquire the existing normalization write gate, taxonomy head lock, receipt fact lock,
and item lock in the established order; recheck source revisions and exact group under
those locks. Assignment CAS and selectable taxonomy checks remain enforced by that
authority. Its returned assignment ID is the only evidence of successful apply.
The public `AssignAsync` transaction cannot by itself provide atomic operation progress.
Do not merely call it and then save progress in a separate transaction.

Serialize operation state changes and chunk execution using a header row lock in a
consistent lock order. Each chunk is at most 100 rows, chosen in persisted ordinal
order from pending rows only. A crash before commit leaves both assignment and
progress unchanged; after commit both replay. If batching transactions per chunk,
the entire chunk must roll back together on an unexpected failure. Expected per-row
conflicts/skips are durable outcomes, not exceptions that lose earlier progress.
Unexpected database/provider errors do not mark success or silently skip work.

`Preview -> Applying -> Completed` requires no pending apply rows before completion.
Restarting Applying continues pending rows. Cancel acquires the same header lock,
transitions Preview/Applying to Cancelled, and stops subsequent chunks. A chunk
already holding the lock can commit before cancel returns; the response shows those
actual assignments. Cancellation does not undo completed rows. Pending rows remain
pending and visible in a cancelled operation, rather than being counted as success
or silently included in skipped totals.

Counts derive from durable row states: applied, conflicted, skipped, and pending.
Do not report selected, attempted, or examined items as applied. Keep undo outcomes
separate so an undone operation still honestly reports what apply originally did.

## Conditional undo

Completed or Cancelled operations can enter Undoing, including after partial apply.
Only rows with a persisted applied assignment ID can be undone. Under the same
assignment locks, require the current assignment ID to equal that exact ID. A later
manual edit blocks undo even if it chose the same category, or changed away and back.
Undo appends the previous category with its own stable operation ID and expected
assignment CAS; it never deletes history or resets sequence numbers.

Undo uses a fresh explicitly captured current taxonomy revision. If the head changes,
or the previous category is no longer selectable there, report conflict and require
a new user decision. Restoring unclassified requires no category node. Do not mutate
taxonomy to make restoration possible. Source text edits alone do not prevent undo
when the exact operation-owned assignment remains current; deleted sources skip.
The first undo request must persist its taxonomy revision and immutable reason for
retries. `Undoing -> Undone` requires all eligible applied rows to have a durable
undo outcome. Conflicts preserve later work and remain visible.

## Acceptance still required for persistence

Real PostgreSQL tests must prove atomic assignment/progress commit and rollback,
duplicate request replay, conflicting request reuse, source edits between preview and
apply, concurrent assignment edits, taxonomy changes, restart after each commit
boundary, cancel/chunk races, and undo versus later edits. HTTP authorization,
problem contracts, bounded request validation, and client selection/review interactions
belong in their later delivery changes. No automatic migration or provider invocation
is part of this workflow.
