# Inactive effect operation identity registry

RECEIPTS-1078 adds a SQL-owned registry and two explicit claim functions. No
production writer, capture trigger, witness seal or portable restore calls them.
This does not establish durable global exclusion or authorize effect execution.

`EffectOperationIdRegistry` reserves one UUID as `ordinary`, `effect` with an
immutable manifest/item/phase owner, or `legacy_blocked`. Ordinary IDs may repeat
across actors, domains and related assignment rows; repeated ordinary claims
succeed. This is not a universal uniqueness constraint on ordinary commands.
Effect claims reserve apply and undo together, reject ordinary IDs, other owners,
phase swaps and blocked history, and roll back both claims on any failure.
An identical owner/phase retry succeeds. Callers must keep claims and corresponding
business writes in the same transaction. The unique index arbitrates concurrent
claims; it waits for commit or rollback rather than trusting a history snapshot.
At stricter isolation callers may need to retry serialization failures. Multiple
pairs in a transaction still require a future global ordering/retry policy.

Migration backfill deduplicates the ordinary command/request UUID columns listed
in the migration, including historical apply/undo IDs, then imports existing effect
owners. Overlaps become `legacy_blocked`, retaining their effect owner and all
original ordinary/effect evidence. An assignment under an existing phase ID might
be legitimate effect history or unrelated ordinary history. The migration does
not infer which. Blocked rows cannot acquire authority through either claim API.
They are immutable like every other registry row. Existing legacy capture and
restore behavior continues unchanged; no legacy rows become typed witnesses.

Run migration with application writers and restores stopped. It locks every
backfill source table in SHARE mode inside the migration transaction before any
read, preventing a concurrent source write during the snapshot. Acquisition can
wait for prior transactions; operational quiescence avoids cycles with their
existing lock order. Locks last through registry creation, backfill and migration
commit. Rollback leaves source history intact. Writers can bypass the registry
again immediately after commit, so this snapshot is not an activation boundary.
The registry deliberately has no EF entity and no source foreign keys: its
immutable reservations must outlive portable source replacement. Downgrade is
refused to retain identity evidence.

Before activation, inventory every writer, direct SQL/import, migration and
restore path, bind the appropriate claim to each atomic write, and refresh the
backfill under a coordinated write/restore fence. Effect-produced assignment
rows must validate their existing effect owner, not attempt an ordinary claim.
The current witness seal does not consult this registry. A future seal must
explicitly reject blocked identities and require the complete owner-bound phase
claims. Existing witnesses gain no new authority from this migration.

Portable v13 remains unchanged. A future restore protocol must retain destination
reservations, preflight all source UUIDs against them, distinguish validated effect
lineage from ordinary reuse, reserve both phases atomically and preserve blocked
history. A clean matching row alone does not prove capture authority. Resolving a
blocked legacy ID requires a separately reviewed evidence/reconciliation protocol
and forward migration; editing/deleting a registry row or treating its old effect
owner as proof is not supported. Neither current restore nor native backup replay
provides the future claim protocol by itself.
