# Backup & Restore

The application supports portable SQLite backups for disaster recovery, migration, and offline archival. Both a REST API and a CLI tool are available.

## What gets backed up

The export (current format `export_version = 4`) includes your domain data plus YNAB
configuration/state and normalized-description settings:

- Accounts
- Cards
- Categories
- Subcategories
- Item Templates
- Receipts (including image file **paths** — see below)
- Receipt Items
- Transactions
- Adjustments
- YNAB configuration and state: selected budget, account mappings, category mappings, and
  sync records (current per-transaction sync state)
- Normalized descriptions and their settings

Soft-deleted records are **excluded** from exports.

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
| Normalized-description embedding vectors | Large, regenerable derived data whose dimension is a build-time constant; repopulated by the embedding pipeline after restore. |
| ASP.NET Identity users and authentication settings | Excluded for security reasons. |

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
- **Import**: File picker with size display, confirmation dialog, and per-entity result summary

## Operational notes

- Backups are self-contained SQLite files — no external dependencies needed to read them
- Import is transactional: if any step fails, the entire import is rolled back
- The CLI tool is useful for automated backup schedules (e.g., cron on the host machine)
- Store backups off-device for true disaster recovery
