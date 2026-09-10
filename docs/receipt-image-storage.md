# Receipt image storage

Receipt originals and processed images are one logical image set. New uploads are stored under an
immutable version directory:

```text
{ImageStoragePath}/{receiptId}/set-{version}/original.{ext}
{ImageStoragePath}/{receiptId}/set-{version}/processed.png
```

`LocalImageStorageService` writes both files into a staging directory using the existing
temp-file-and-rename primitive. Only after both writes succeed does it atomically rename the staging
directory to its final version. The receipt row then switches both database paths in one serialized
write. A failed file write or database update therefore leaves the previously referenced complete
set readable; concurrent uploads cannot combine variants from different versions.

Previous versions are left for the reconciler. Deleting them in the request path would race a
backup import that restores an older image path after the upload transaction commits.

## Purge and recovery cleanup

Empty Trash only commits the database purge. Its now-unreferenced receipt directories are removed
by the reconciler after the grace period, so a concurrent backup import cannot recreate a receipt
whose directory is then deleted by a stale purge request.

`ReceiptImageCleanupService` is the recovery path for interruption and transient storage failures.
Once per hour it reads image paths from all live and soft-deleted receipts and removes unreferenced
image-set/staging directories older than one hour. The grace period protects a complete set in the
small window between filesystem publication and the database reference switch. Legacy files stored
directly under a receipt directory are cleaned individually so cleanup cannot delete a newer
versioned set beside them.

Upload publication, backup import, and each reconciliation cycle share a transaction-scoped
PostgreSQL advisory lock. An upload holds it from before filesystem publication through its durable
path switch; the reconciler holds it from its durable reference snapshot through filesystem
deletion; an import holds it for its complete database transaction. This prevents either a new set
from being deleted before its path switch or an old path from becoming current between the
reconciler's snapshot and deletion, including across API instances.

Cleanup logs deleted set/directory counts and logs cycle failures. It is idempotent and retries on
the next interval, so a process stop after a database commit leaves recoverable orphaned storage,
not lost referenced images.
