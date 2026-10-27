# Purchase classification history

RECEIPTS-1046 adds immutable classification assignments independently of receipt
text and normalized product identity. Each event pins a complete taxonomy revision;
later category moves and renames do not change its recorded path. Purchase dates
remain separate from the UTC time an assignment was recorded.

Authenticated callers can read an item's paginated classification history. Explicit
assignment writes require the expected taxonomy revision, expected assignment ID,
and a client operation ID. Both nullable fields (`categoryId` and
`expectedAssignmentId`) must be present. A null category is an explicit
unclassified assignment; an active parent category is a valid broad assignment.

Receipt-item and complete-receipt writes accept an optional classification intent.
Omission preserves existing classification. Explicit aggregate choices also require
the existing `clientRequestId`, so retries cannot accidentally change receipt facts
under an already-completed classification operation. Receipt facts, normalization
decisions, classification history, and their replay records commit together.

## Rollout boundary

`Classification:WritesEnabled` defaults to `true` after the classification cutover.
Explicit classification writes still require completed legacy backfill and a current assignment for every
existing receipt item. Set `Classification__WritesEnabled=false` during maintenance
or until the upgrade, writer reconciliation, and portable backup round-trip have
been verified. Backup validation is an operator rollout step, not a database flag.
Ordinary receipt saves without classification intent continue while writes are disabled.
No external classification provider is required by either path.

Initial legacy assignments must record observations at migration time, not invent
history at the purchase date. Migration must include trash, retain original labels,
report ambiguity, and validate the current-assignment projection against immutable
history. Projection reconstruction and recovery evidence belong to that migration
gate. See [legacy taxonomy migration](legacy-taxonomy-migration.md) for current
legacy backup import and cutover procedures.

History migrations deliberately refuse destructive downgrade. Before activation,
disable new callers and retain the additive tables. After activation, recovery must
preserve assignment history through a verified compatible backup or a forward fix.

RECEIPTS-1051 and RECEIPTS-1053 separately own scoped, resumable historical
reclassification into deeper tiers, with preview and conditional undo. Neither
taxonomy edits nor initial legacy migration silently reclassify older purchases.
