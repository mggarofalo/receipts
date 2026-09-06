# Backup & Restore

The application supports portable SQLite backups for disaster recovery, migration, and offline archival. Both a REST API and a CLI tool are available.

## What gets backed up

The export (current format `export_version = 5`) includes your domain data plus YNAB
configuration/state and normalized-description settings:

- Accounts
- Cards
- Categories
- Subcategories
- Item Templates, including declared canonical-description links
- Receipts (including image file **paths** — see below)
- Receipt Items, including canonical-description links and recorded match scores
- Transactions
- Adjustments
- YNAB configuration and state: selected budget, account mappings, category mappings, and
  sync records (current per-transaction sync state)
- Normalized descriptions, display labels, review/rejection status, recorded neighbour context, and settings
- Accepted duplicate pairs whose two receipts are included in the export

Soft-deleted records are **excluded** from exports. An accepted pair may survive a receipt's soft deletion in the live database, but a portable backup excludes that pair if either receipt is excluded. Export does not alter the source acceptance.

Receipt image **binaries** are stored outside the database and are **not** included — only
their file paths are backed up. Back the image files up separately.

### Deliberately excluded from backup

The following tables are intentionally left out of the backup. This is a conscious decision
(RECEIPTS-802), not an oversight — a backup is meant to restore your **data and state**, not
the history of how it got there or values a service can regenerate:

| Table | Why it is excluded |
| --- | --- |
| `AuditLogs`, `AuthAuditLogs` | Append-only audit/activity logs. Re-importing historical log rows onto another instance would misrepresent when actions actually occurred there. |
| `YnabSyncEvents` | The YNAB sync activity log — the same class of data as the audit logs (an append-only history of push attempts, **not** state). Distinct from `YnabSyncRecords`, the current sync state, which **is** included above. |
| `YnabServerKnowledge` | The YNAB delta-sync cursor. It is re-fetchable from YNAB on the next sync (regenerable derived data, like the omitted embedding vectors), so restoring a stale value would only risk a bad delta window. |
| Normalized-description embedding vectors | Regenerable derived data whose dimension is a build-time constant; see the vector recovery limitation below. |
| ASP.NET Identity users and authentication settings | Excluded for security reasons. |

## Format compatibility and curation

Format v5 preserves user decisions separately from vectors: canonical item/template assignments, human display labels, rejected-description tombstones, neighbour comparison history, and accepted duplicate pairs. Canonical rows are restored before item/template references and before their self-referencing neighbour links. A removed neighbour can legitimately leave a historical score with no remaining neighbour ID; that history is preserved.

Import remains an **upsert**, not a replacement of the target database. Records missing from a file are not deleted from the target. For an accepted pair already present under a different local ID, the existing ID is retained and its acceptance timestamp is updated. A fresh restore preserves the exported pair ID. Reusing a pair ID for different receipts is rejected, and constraint failures roll back the complete import. Restoring exchanged canonical names or display labels between included rows is supported; audit history records the original and final values. Names that conflict with an unrelated target row still reject the import.

Version 5 curation references must resolve within the backup itself: item/template links and neighbour IDs require included canonical rows, and accepted pairs require both included receipts. Existing target data cannot supply a missing curation dependency.

Version 5 fields are authoritative, including explicit nulls. Versions 1–4 still import using their original schemas. On a fresh target, fields those formats never carried default to null and no acceptance rows are invented. On an existing target, absent labels, template declarations, and acceptance rows are retained. An item retains an absent canonical link/score only while its raw description is unchanged; importing different text clears that stale classification. A changed canonical matching name also invalidates its existing vector/model version and, for legacy files without replacement evidence, its neighbour comparison history.

Missing version metadata means legacy version 1. Malformed or unsupported version values are rejected. Version 5 requires its complete table inventory; missing tables cannot silently turn a restore into partial recovery. Use an importer that explicitly supports the file's format version.

Canonical embedding vectors are not in the portable file. Freshly restored canonical rows have no vectors; existing vectors survive only when their matching text is unchanged. Automatic canonical-vector rebuilding is separate recovery work (RECEIPTS-959). Until that rebuilding is available, similarity search can be incomplete even though restored grouping, labels, and curation remain available.

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

Accepts a single file upload (`.sqlite`, `.sqlite3`, or `.db`, max 100 MB). The import uses **upsert** semantics: existing records are updated by primary key, new records are created. Previously soft-deleted records are restored if they appear in the backup.

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

## UI

The **Backup & Restore** page is available at `/admin/backup` (admin users only). It provides:

- **Export**: One-click download with progress spinner
- **Import**: File picker with size display, confirmation dialog, and created/updated totals

## Transfer and refresh ownership

Both browser transfers use the shared API client, including authentication, native 401 refresh/replay, connection-origin headers and session cancellation. Each has one five-minute deadline covering the initial request, any refresh wait/replay and response-body consumption. Ordinary requests retain their 30-second deadline. Import serializes one FormData upload; replay preserves its filename, boundary and bytes. Export keeps its Blob internal to the download action and preserves the public void result and callbacks.

`useBackupImport` owns the import mutation, local feedback and restored-query repair. The page owns the file picker and confirmation dialog. A rejected import retains the selected file for retry; success clears it. Both transfer hooks select the paired local request/cache error policy, so a failed transfer does not navigate away or produce duplicate generic feedback. A timeout or connection loss can occur after the server committed; there is no automatic retry of an uncertain import.

The API enqueues a `backup-import` change only after the import service returns its committed result. It does so even when the response counters are zero or the request is cancelled immediately after commit: restored settings may have changed and other sessions still need refresh. Validation and precommit failures, cancellation before commit, and export do not emit this event. The existing notifier queues delivery; it does not await a hub send or provide a durable event log.

Local success repairs the current session's restored-data queries even without SignalR. The remote event uses the same repair union, including ledger, taxonomy, templates, normalization settings and all YNAB queries. Authentication, users, roles and API-key queries are excluded. Repair cancels matching in-flight reads before invalidating, including reads still running after navigation. An old result therefore cannot clear staleness and remain infinitely fresh after restore. It marks inactive entries stale and refetches active ones once through a union predicate. Session guards apply after asynchronous cancellation as well as at entry. Reconnection uses this same primitive for unknown missed changes.

Refreshing active YNAB queries may read the remote service; restore does not issue remote YNAB writes. Some optional read error owners remain tracked in RECEIPTS-950. Cache repair does not rebuild excluded vectors or image binaries, and it does not establish the still-pending synchronous producer and background completion guarantees in RECEIPTS-948/959/964.

## Operational notes

- Backups are self-contained SQLite files — no external dependencies needed to read them
- Import is transactional: if any step fails, the entire import is rolled back
- The CLI tool is useful for automated backup schedules (e.g., cron on the host machine)
- Store backups off-device for true disaster recovery
