# Receipt photos retired

Receipts no longer uploads, processes, stores, serves, or cleans up receipt
photos. Documents belong in the owner's Paperless-ngx archive; this does not add
a Paperless integration. The former `POST /api/receipts/{receiptId}/image`
operation and its response schema are removed intentionally (RECEIPTS-994).
There is no replacement upload endpoint. Existing receipt and backup APIs remain.

## Existing files and metadata

Upgrading does not delete, move, or inspect existing image files. No filesystem
cleanup service runs. The former `ImageStorage:Path` setting is ignored. The
previous default was `ImageStorage` beneath the API application directory; a
configured location may be elsewhere. Previous uploads used these layouts:

```text
{storage}/{receiptId}/set-{version}/original.{ext}
{storage}/{receiptId}/set-{version}/processed.png
{storage}/{receiptId}/original.{ext}
{storage}/{receiptId}/processed.png
```

Retain or archive those directories manually if needed. Portable SQLite backups
never included the image binaries. The nullable receipt columns
`OriginalImagePath` and `ProcessedImagePath` remain as inert archival metadata,
not an active attachment feature. Backups still round-trip them without opening
any referenced path. Pre-v4 backups lack these fields and preserve existing
destination metadata on import. No database migration or backup version change
is required. Ordinary receipt edits continue to preserve this metadata.

## Import coordination and deployment

`BackupImportLock` owns serialization of complete portable imports. It retains
the existing local and PostgreSQL advisory lock key so concurrent imports, and
older instances during a rolling upgrade, coordinate as before. The lock has no
filesystem behavior. Stop older API instances to retire their upload and cleanup
workers too; a new instance cannot disable a worker in an old process.

SkiaSharp, its native runtime assets, and the image-only license deployment are
removed. ONNX embeddings remain independent and still require `libgomp1` in the
Linux image. Historical migrations and audit evidence are retained.
