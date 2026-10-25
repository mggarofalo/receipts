# Backup & Restore

The application supports portable SQLite backups for disaster recovery, migration, and offline archival. Both a REST API and a CLI tool are available.

## What gets backed up

The export (current format `export_version = 10`) includes your domain data plus YNAB
configuration/state and normalized-description settings:

- Accounts
- Cards
- Categories
- Subcategories
- Item Templates, including declared canonical-description links
- Receipts (including inert legacy image-path metadata; see below)
- Receipt Items, including canonical-description links and recorded match scores
- Transactions
- Adjustments
- YNAB configuration and state: selected budget, account mappings, category mappings, and
  sync records (current per-transaction sync state)
- Normalized descriptions, display labels, review/rejection status, recorded neighbour context, and settings
- Accepted duplicate pairs, including deleted decisions and their receipt dependencies
- Normalization source mappings, catalog operation observations and membership,
  purchase assignment provenance and review/exclusion evidence, and receipt-write
  replay evidence (in `normalization_catalog`)
- Complete immutable taxonomy identities, revisions and tree snapshots, taxonomy
  operations, purchase classification history and operation results, and the current
  classification projection (in `taxonomy_archive`)
- Initial legacy-migration plans, table-qualified crosswalks, item outcomes and
  original source evidence, including exact nullable strings and observation boundaries
- Explicit active/trash state for every soft-deletable business row, including
  deletion time, user/API-key actor and cascade parent (in `portable_deletion_state`)

Format 10 includes **all trash** as well as active data: categories, subcategories,
templates, receipts, receipt items, transactions, adjustments, YNAB sync records and
accepted duplicate pairs. Restore preserves their recorded deletion state rather
than reviving every imported row. Formats 1–9 did not carry portable trash metadata;
restoring one cannot recover rows its exporter omitted. Export never changes the source.

Receipt photos are retired. Existing image-path metadata remains in backups for
archival compatibility; image binaries were never included. Imports do not access
those paths or modify files. See [photo retirement and existing files](receipt-image-storage.md).

### Deliberately excluded from backup

The following tables are intentionally left out of the backup. This is a conscious decision
(RECEIPTS-802), not an oversight. Portable backups preserve business decisions and
their authoritative history, including taxonomy/classification and migration evidence.
They exclude operational/security logs, credentials and regenerable values:

| Table | Why it is excluded |
| --- | --- |
| `AuditLogs`, `AuthAuditLogs` | Append-only audit/activity logs. Re-importing historical log rows onto another instance would misrepresent when actions actually occurred there. |
| `YnabSyncEvents` | The YNAB sync activity log — the same class of data as the audit logs (an append-only history of push attempts, **not** state). Distinct from `YnabSyncRecords`, the current sync state, which **is** included above. |
| `YnabServerKnowledge` | The YNAB delta-sync cursor. It is re-fetchable from YNAB on the next sync (regenerable derived data, like the omitted embedding vectors), so restoring a stale value would only risk a bad delta window. |
| Normalized-description embedding vectors | Regenerable derived data whose dimension is a build-time constant; see the vector recovery limitation below. |
| ASP.NET Identity users and authentication settings | Excluded for security reasons. |

## Format compatibility and curation

Format v5 preserves user decisions separately from vectors: canonical item/template assignments, human display labels, rejected-description tombstones, neighbour comparison history, and accepted duplicate pairs. Canonical rows are restored before item/template references and before their self-referencing neighbour links. A removed neighbour can legitimately leave a historical score with no remaining neighbour ID; that history is preserved.

Business rows use **upsert** semantics; ordinary omissions do not delete target
rows. This does not authorize merging arbitrary installations. Format 10 admits a
fresh taxonomy foundation or a destination whose complete immutable taxonomy,
assignment, operation and migration history is an exact prefix of the archive.
Conflicting/divergent history, missing destination history, a head rewind or an
older per-item classification is rejected before business upserts. Matching names
do not establish lineage. Restore appends missing sealed history and rebuilds
current assignments from its latest entries; database-local transaction stamps
are recreated, not imported. Use a fresh isolated target when lineage is uncertain.

Format 10 preserves accepted-pair IDs and active/trash state exactly, including
multiple historical decisions for the same receipt pair. If an incoming active
decision has a different ID from the target's active decision for those receipts,
the displaced target decision is retained in trash and the source ID becomes active.
Reusing a pair ID for different receipts is rejected. Formats 5–9 retain the older
semantic pair-matching behavior (reuse a local pair ID when present); their target
admission is now subject to the legacy conversion restrictions below.

Constraint failures roll back the complete import. Restoring exchanged canonical
names or display labels between included rows is supported; local audit records
describe the restore changes. Names conflicting with an unrelated target row still
reject the import. Excluded source audit logs are never replayed.

### Formats 1–9: fresh-target conversion

Older portable imports automatically derive the initial taxonomy and assignments
from the file's exact category, subcategory and receipt-item evidence, in the same
transaction as the business import. They require untouched empty genesis, no
receipt items (including trash), and at most untouched application seed suggestions.
Unrelated populated or versioned destinations reject before business mutation,
even with classification writes disabled. A byte-identical previously imported
file is a validated no-op and preserves subsequent edits; a repacked or changed
file has a new SHA-256 and is not that replay. See
[older portable backup conversion](taxonomy-revisions.md#older-portable-backup-conversion)
for provenance and observation-time semantics. For an existing legacy PostgreSQL
database, use the separate [in-place migration procedure](legacy-taxonomy-migration.md)
after applying schema migrations; do not import an old portable file over it.

### Curation compatibility

The explicit exception to ordinary upserts is format 9/10 offline consolidation authority: reviewed
`consolidation` records retire named canonical IDs and redirect their purchase,
template and mapping links to an included active survivor before normal upserts.
This includes links on purchases in trash. Purchases absent from the artifact
retain the survivor link but require review again; their old review/mapping
authority is cleared. Repeated import skips already absent retired IDs. Ordinary
omissions never authorize retirement. This authority is consumed from the artifact,
not stored as replayable operation history or included in later app exports.
See [offline backup normalization](offline-backup-normalization.md) for the reviewed
manifest, source-freeze and artifact-retention requirements.

Version 5 and later curation references must resolve within the backup itself: item/template links and neighbour IDs require included canonical rows, and accepted pairs require both included receipts. Existing target data cannot supply a missing curation dependency.

Version 5 and later curation fields are authoritative, including explicit nulls.
Versions 1–4 still read their original schemas under the fresh-target conversion
rules above. Fields those formats never carried default to null and no acceptance
rows are invented. A changed canonical matching name invalidates its existing
vector/model version and, for legacy files without replacement evidence, its
neighbour comparison history. Product normalization links are separate from
taxonomy classification assignments.

Missing version metadata means legacy version 1. Malformed or unsupported version
values are rejected. Versions 5–10 require the complete base table inventory;
versions 7–10 also require `normalization_catalog`, and version 10 requires
`taxonomy_archive` and `portable_deletion_state`. Missing tables cannot silently
turn a restore into partial recovery. Use an importer that explicitly supports
the file's format version.

Formats 6, 7 and 8 remain supported alongside formats 1–5. Format 7 adds catalog
mappings, operation observations, membership and purchase assignment provenance;
format 8 adds purchase review/exclusion and receipt-write replay evidence. Older
files do not invent review authority. Catalog payloads and their dependencies are
validated, and format 9 adds only explicit consolidation authority. Restored
normalization operations are inspect-only (`restored`), with fresh revisions;
create a new preview to change restored purchases. Existing operations are fenced
before restore so pre-restore inference cannot act on restored data. These portable
business observations are distinct from the excluded operational audit logs.

Offline inspection and reviewed normalization support current format 10 as well
as formats 6, 8 and 9. A v10 input remains v10; its taxonomy, classifications,
migration evidence and deletion state are validated and preserved unchanged.
Older supported inputs produce v9 and must follow fresh-target conversion on
import. Product normalization does not confer authority to alter classification.
See [offline backup normalization](offline-backup-normalization.md).

Canonical embedding vectors are not in the portable file. Freshly restored canonical rows have no vectors; existing vectors survive only when their matching text is unchanged. When the embedding service is configured, the background worker rebuilds missing or outdated vectors for eligible canonical rows before other semantic sources. Similarity search can be incomplete until rebuilding finishes; restored grouping, labels, and curation remain available.

## Export consistency and file ownership

Portable exports read all included PostgreSQL tables from one read-only Repeatable Read snapshot, established by the first source query. Concurrent transactions can continue writing; their later changes appear in a subsequent backup, rather than mixing old parent values with new child values in the current file. The destination SQLite transaction enforces foreign keys and completes before the file is returned.

The export service disposes both database connections and transactions before returning its temporary file. A successful file belongs to the API/CLI caller. The API removes it after delivery; the CLI moves it to an explicit output path, or retains the returned path as its output when no destination is supplied. On failure or cancellation, the service disposes its resources and removes its incomplete file. The SQLite export connection is not pooled, so deleting a failed export does not depend on clearing unrelated connection pools.

This source snapshot guarantee is verified against PostgreSQL; InMemory tests cover serialization behavior only. It follows [PostgreSQL Repeatable Read semantics](https://www.postgresql.org/docs/current/transaction-iso.html#XACT-REPEATABLE-READ).

## REST API

Both endpoints require the **Admin** role.

### Export

```
POST /api/backup/export
Authorization: Bearer <token>
```

Returns a SQLite database file as `application/octet-stream` with filename `receipts-backup-{yyyyMMdd-HHmmss}.db`.

### Import

```
POST /api/backup/import
Content-Type: multipart/form-data
Authorization: Bearer <token>
```

Accepts a single file upload (`.sqlite`, `.sqlite3`, or `.db`, max 100 MB).
Target admission and upsert rules are described above. Format 10 preserves explicit
active/trash state; older formats contain no such state and perform fresh-target
conversion (or validated exact-file replay).

Response:

```json
{
  "accountsCreated": 2, "accountsUpdated": 1,
  "categoriesCreated": 3, "categoriesUpdated": 0,
  "subcategoriesCreated": 5, "subcategoriesUpdated": 2,
  "itemTemplatesCreated": 4, "itemTemplatesUpdated": 1,
  "receiptsCreated": 10, "receiptsUpdated": 0,
  "receiptItemsCreated": 30, "receiptItemsUpdated": 5,
  "transactionsCreated": 10, "transactionsUpdated": 0,
  "adjustmentsCreated": 3, "adjustmentsUpdated": 0,
  "acceptedDuplicatePairsCreated": 0, "acceptedDuplicatePairsUpdated": 0,
  "totalCreated": 67, "totalUpdated": 9
}
```

## CLI tool (DbExporter)

For scripted or cron-based backups without the web API:

```bash
# Default output path (temp directory)
dotnet run --project src/Tools/DbExporter

# Custom output path
dotnet run --project src/Tools/DbExporter -- /backups/receipts.db
```

Requires database connection via `POSTGRES_*` environment variables or Aspire connection string.

This is an export-only source/SDK tool. The production Dockerfile ships DbMigrator,
DbSeeder and the taxonomy maintenance tool, but not DbExporter. There is no
portable-import CLI documented here; use the Admin REST endpoint or UI for portable
imports. Run containerized schema/backfill maintenance through the explicit
[entrypoint overrides](legacy-taxonomy-migration.md#container-maintenance-and-restart-gate).

## UI

The **Backup & Restore** page is available at `/admin/backup` (admin users only). It provides:

- **Export**: One-click download with progress spinner
- **Import**: File picker with size display, confirmation dialog, and created/updated totals

## Transfer and refresh ownership

Both browser transfers use the shared API client, including authentication, native 401 refresh/replay, connection-origin headers and session cancellation. Each has one five-minute deadline covering the initial request, any refresh wait/replay and response-body consumption. Ordinary requests retain their 30-second deadline. Import serializes one FormData upload; replay preserves its filename, boundary and bytes. Export keeps its Blob internal to the download action and preserves the public void result and callbacks.

`useBackupImport` owns the import mutation, local feedback and restored-query repair. The page owns the file picker and confirmation dialog. A rejected import retains the selected file for retry; success clears it. Both transfer hooks select the paired local request/cache error policy, so a failed transfer does not navigate away or produce duplicate generic feedback. A timeout or connection loss can occur after the server committed; there is no automatic retry of an uncertain import.

The API enqueues a `backup-import` change only after the import service returns its committed result. It does so even when the response counters are zero or the request is cancelled immediately after commit: restored settings may have changed and other sessions still need refresh. Validation and precommit failures, cancellation before commit, and export do not emit this event. The existing notifier queues delivery; it does not await a hub send or provide a durable event log.

Local success repairs the current session's restored-data queries even without SignalR. The remote event uses the same repair union, including ledger, taxonomy, templates, normalization settings and all YNAB queries. Authentication, users, roles and API-key queries are excluded. Repair cancels matching in-flight reads before invalidating, including reads still running after navigation. An old result therefore cannot clear staleness and remain infinitely fresh after restore. It marks inactive entries stale and refetches active ones once through a union predicate. Session guards apply after asynchronous cancellation as well as at entry. Reconnection uses this same primitive for unknown missed changes.

Refreshing active YNAB queries may read the remote service; restore does not issue remote YNAB writes. Some optional read error owners remain tracked in RECEIPTS-950. Cache repair does not rebuild excluded vectors or image binaries; vector rebuilding is owned by the background worker described above.

## Operational notes

- Portable SQLite backups exclude identity/security state. For an in-place
  deployment recovery point, also retain a verified PostgreSQL backup and the
  deployment's separately protected configuration/secrets. The Compose
  `scripts/backup.cs` and `scripts/restore.cs` operate on PostgreSQL `.sql.gz`
  dumps, not portable SQLite files. Follow the [cutover procedure](legacy-taxonomy-migration.md)
  before using deployment restart or rollback automation.
- Backups are self-contained SQLite files — no external dependencies needed to read them
- Import is transactional: if any step fails, the entire import is rolled back
- The CLI tool is useful for automated backup schedules (e.g., cron on the host machine)
- Store backups off-device for true disaster recovery
