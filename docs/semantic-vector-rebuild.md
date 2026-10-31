# Semantic vector rebuilding

Receipts treats embeddings as rebuildable projections. Canonical-description names, display
labels, review status, template links, receipt-item links, and audit history remain durable; the
vectors derived from their text do not belong in portable backups.

## Embedding-space identity

Every stored vector carries `OnnxEmbeddingService.EmbeddingSpaceFingerprint`. The fingerprint
includes the pinned ONNX digest, the ONNX Runtime and tokenizer assembly versions, the vocabulary
digest, maximum token count, pooling strategy, and L2-normalization contract. Change the fingerprint
whenever any input can change vector coordinates.

**Runtime and tokenizer versions stay in the fingerprint (decided in RECEIPTS-1103).** A runtime
upgrade can change kernels and float rounding, and a tokenizer upgrade can split the same text into
different tokens. Either can move vectors without any model change, and mixing spaces silently
degrades nearest-neighbour results. That costs more than a rebuild, which is bounded and resumable.
The cost is that **every `Microsoft.ML.OnnxRuntime` or `Microsoft.ML.Tokenizers` bump re-embeds the
whole corpus.** To keep that visible, `EmbeddingSpaceFingerprintTests.EmbeddingSpaceFingerprint_IsPinned`
pins the exact string. It is expected to fail on every such bump. Update it only as a deliberate
decision to rebuild, and say so in the PR, so a dependency update cannot start a rebuild unnoticed.

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

When searchable catalog coverage at the current fingerprint is below 95%, the embedding worker
logs one warning per fingerprint and process. The warning gives the ready and total counts and the
fingerprint, so a rebuild in progress is not mistaken for a model regression. Until coverage
recovers, semantic suggestions are incomplete.

Admins can read `GET /api/normalized-descriptions/embedding-coverage`. It reports current and total
counts for searchable canonical rows and eligible item sources, pending counts, the admitted
fingerprint, and `isComplete`.

Read-only exact canonical-name lookup and approved scoped receipt mappings do not depend on vectors
and remain available throughout a rebuild. Receipt normalization does not create identities or
accept similarity matches automatically. Semantic ANN and
hybrid contributions are intentionally partial until `isComplete` becomes true; they never use
vectors from an obsolete embedding space or from source text that has since changed.
