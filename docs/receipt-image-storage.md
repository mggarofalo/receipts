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

## Photo processing and codec deployment

Photo uploads accept JPEG and PNG, identified from bytes rather than trusting the content type.
Infrastructure uses SkiaSharp 4.153.1 for decoding, rotation and PNG encoding. Application retains
its image-processing port and immutable image-set publication use case. No storage path, database
column, backup format or HTTP contract changes with the codec replacement; existing stored images
remain readable and original uploads are retained byte-for-byte.

The processor checks PNG header dimensions before codec creation and all codec dimensions before
pixel allocation: each dimension is at most 8,000 and the total is at most 24 million pixels. It
requires a complete successful decode of frame zero. These are pixel bounds, not a claim of a hard
256 MB process-memory budget. Native codec calls are synchronous; cancellation is checked around
them and once per row during the managed grayscale, local threshold and skew calculations.

The existing Rec.709 grayscale conversion, 15-by-15 local mean threshold and half-degree projection
search from -10 to +10 degrees remain. Uniform images now retain their dimensions instead of
acquiring a meaningless rotation; equal-scoring candidates prefer the angle closest to zero.
Skia's cubic rotation can produce slightly different edge pixels and rounded output dimensions
than ImageSharp. Processed PNGs are genuinely single-channel 8-bit grayscale. Transparent source
pixels retain their color luminance without alpha compositing, as before. Original bytes are not
transformed.

SkiaSharp's managed binding is MIT licensed. The API explicitly references the matching
`SkiaSharp.NativeAssets.Linux.NoDependencies` package for the Linux container; Win32 native assets
arrive with the managed package. No font discovery or GPU functionality is required. The obsolete
`libgdiplus` container dependency is removed; ONNX retains `libgomp1`. The API copies
the pinned package's license and complete native third-party notices to `licenses/SkiaSharp` in
build and publish output. Keep managed and native package versions aligned, review NuGet security
advisories, and verify real native decode/encode on Windows and Linux after updates. This does not
imply that native image decoding is free of security risk.

### Removed unused PDF capability

The former PDF conversion port and implementation had no production caller beyond DI registration.
There is no PDF upload endpoint, working OCR pipeline or Paperless integration; the API-key name
example is not an integration. They and the PdfPig dependency have been removed. This deletes an
unused internal capability, not supported external PDF behavior. Future PDF/OCR work must establish
an actual application use case, supported contracts and resource/security limits before choosing a
parser or renderer.

SkiaSharp was selected over Magick.NET's broader format/delegate surface and NetVips' additional
libvips pipeline for this narrow JPEG/PNG requirement. System.Drawing is unsuitable for the Linux
server. Primary references: [SkiaSharp license](https://github.com/mono/SkiaSharp/blob/main/LICENSE.md),
[release](https://github.com/mono/SkiaSharp/releases/tag/v4.153.1),
[native deployment](https://github.com/mono/SkiaSharp/blob/main/documentation/dev/packages.md),
[Magick.NET](https://github.com/dlemstra/Magick.NET), and [NetVips](https://github.com/kleisauke/net-vips).
