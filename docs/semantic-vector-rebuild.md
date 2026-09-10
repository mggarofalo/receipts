# Semantic vector rebuilding

Receipts treats embeddings as rebuildable projections. Canonical-description names, display
labels, review status, template links, receipt-item links, and audit history remain durable; the
vectors derived from their text do not belong in portable backups.

## Embedding-space identity

Every stored vector carries `OnnxEmbeddingService.EmbeddingSpaceFingerprint`. The fingerprint
includes the pinned ONNX digest, tokenizer implementation and vocabulary digest, maximum token
count, pooling strategy, and L2-normalization contract. Change the fingerprint whenever any input
can change vector coordinates.

ANN and hybrid-search queries use only the current fingerprint. During a rollout, old vectors can
remain stored until the worker reaches them, but they cannot be compared with queries from the new
embedding space.

## Rebuild behavior

`EmbeddingGenerationService` processes at most 50 rows per cycle. It prioritizes searchable
canonical descriptions, then item templates and receipt items. Rows with a missing vector, changed
source text, or obsolete fingerprint are eligible. Each cycle generates outside the database
transaction, then locks and re-reads the source rows before writing; edits or deletions that race
inference are skipped and picked up by a later cycle.

This makes the operation resumable after a process interruption and automatic after either:

- a portable-backup restore, which deliberately imports no vectors; or
- an embedding fingerprint change, which makes every prior projection stale.

The worker never recreates or relinks canonical descriptions. Rebuilding therefore preserves
curated names, statuses, links, and other durable decisions.

## Readiness and incomplete coverage

Admins can read `GET /api/normalized-descriptions/embedding-coverage`. It reports current and total
counts for searchable canonical rows and eligible item sources, pending counts, the admitted
fingerprint, and `isComplete`.

Exact case-insensitive canonical-name matching does not depend on vectors and remains available
throughout a rebuild. The receipt-item fuzzy resolver pauses while canonical coverage is incomplete
so an excluded old vector cannot turn an ANN miss into a duplicate durable row. Semantic ANN and
hybrid contributions are intentionally partial until `isComplete` becomes true; they never use
vectors from an obsolete embedding space or from source text that has since changed.
