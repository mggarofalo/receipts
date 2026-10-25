# Taxonomy revision foundation

RECEIPTS-1045 adds a separate application-wide `taxonomy` schema and authenticated current/historical reads. Admin changes atomically append a complete immutable tree and advance a single locked head. Each operation persists actor, reason, time, request hash and result in the same transaction. Retries return the original revision; no-op results have zero affected nodes and retain the existing head.

The genesis revision is an empty foundation recorded on 2026-10-25. It is not an
import of legacy suggestions and makes no historical claim about receipts.
Category/Subcategory suggestions and receipt string snapshots remain separate
from the immutable taxonomy. Schema migration alone does not backfill
classifications. Use the [legacy migration runbook](legacy-taxonomy-migration.md)
for in-place preflight/apply and deployment gates; keep classification writes
disabled until the destination and deployed writers pass those gates.

POST `/api/taxonomy/changes` accepts create, rename, move, define, archive and restore changes. Names compare exactly and ordinally; active siblings must be unique. Move carries descendants; archive archives the complete subtree. Restore is explicit per node and needs active ancestors in the resulting tree. Responses include every known identity, including archived nodes, so callers can reconstruct exact historical paths. No operational depth limit is imposed.

Names contain at most 200 Unicode scalar values; definitions contain at most 4000. These bounds apply to API writes, domain validation and PostgreSQL constraints. The indexed name bound keeps even four-byte characters within PostgreSQL index entry limits. Change kind must be explicitly present in JSON.

Migration is additive. PostgreSQL rejects updates/deletes of identities, revisions,
snapshot rows and completed operations. A revision records its database creation
transaction; snapshot inserts require that same transaction, sealing every snapshot,
including empty genesis, at commit. The database assigns the stamp; callers cannot
supply it. Full snapshot creation and head advancement remain in one transaction.
Automatic down migration deliberately fails to preserve history. Before cutover,
disable new callers and leave the additive tables; restore a verified backup only
as an explicit recovery operation. Old receipt writers that cannot preserve
classifications are not a valid rollback after cutover.

## Portable history (format 10)

Current exports include complete taxonomy revisions/snapshots and operation
results, purchase assignment history and its current projection, initial migration
plans/crosswalks/item outcomes and original evidence. Active/trash business rows
retain deletion actors, times and cascade relationships. These are authoritative
business history, distinct from excluded operational/security logs and authentication
state.

Restore appends missing immutable history into a fresh foundation or a destination
whose complete history is an exact prefix of the archive. It rejects conflicting,
divergent or stale history before business mutation, never matches unrelated nodes
by name, and never rewinds later classifications. Current assignments are rebuilt
from latest history and local database transaction stamps are recreated. Offline
product normalization preserves v10 taxonomy, classification, migration and
deletion records unchanged. See [backup and restore](backup-restore.md) for target
restrictions, pair identities, compatibility and excluded data.

## Category tree interface (RECEIPTS-1048)

Authenticated users can browse and search complete category paths at `/taxonomy`.
Duplicate labels remain distinct through their full paths. Archived categories are
available with **Show archived**. **Revision details** exposes the actor, reason,
time and immutable revision link; **Previous revision** opens a read-only snapshot.

Administrators can create, rename, move, archive and restore categories. Each edit
shows its affected category count before saving. Moving carries descendants;
archiving includes the entire subtree; restoring affects only the chosen node and
requires active ancestors. The editor pins its original revision and preserves an
uncertain request's operation ID for retry. A conflict requires an explicit refresh;
the draft survives while the administrator reviews the updated scope before saving.
Validation failures leave the draft editable. No receipt assignments are rewritten.

The existing `/categories` and `/subcategories` routes, query parameters and library
links remain compatible until the legacy migration/retirement gates. The tree links
to those views even when genesis is empty; it does not imply legacy receipt data has
been imported or hide that data. Taxonomy management does not invoke a provider.


### Older portable backup conversion

Portable v1–9 imports derive initial classification only from the file's exact
category, subcategory and receipt-item evidence. The source dataset is the export
version plus SHA-256 of the complete SQLite file; a deterministic UUID identifies
the import operation. The initial revision and assignments use `legacy_import`
provenance with the server actor `legacy_backup_import`, immutable source strings,
and the first successful import observation time, never the purchase date. The
existing initial-migration planner and transactional append path create the
snapshot, crosswalks, outcomes, current assignments and readiness marker together
with the imported business data. Earlier classification history remains unknown.

A destination must have untouched genesis and no receipt items. Only exact,
untouched application seed suggestions may already exist; seed rows absent from
the file stay in the suggestion library and do not become imported taxonomy
evidence. Other populated legacy targets and versioned targets reject before any
business upsert, independent of `Classification:WritesEnabled`. Restore those
files into a fresh database; merging unrelated source sets requires reviewed
reconciliation. A byte-identical previously imported file is a no-op: its stored
plan is revalidated with the original observation boundary and returns zero
created/updated counts, preserving any later receipt edits and assignments. A
modified/repacked file has a different dataset fingerprint and does not qualify
as that replay.
