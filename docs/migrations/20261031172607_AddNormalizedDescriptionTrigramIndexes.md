# Migration: AddNormalizedDescriptionTrigramIndexes (RECEIPTS-1094)

**Migration id:** `20261031172607_AddNormalizedDescriptionTrigramIndexes`
**Date:** 2026-10-31

This migration changes indexes only. No row is read or rewritten.

## What it does

It adds two GIN trigram indexes on `matching."NormalizedDescriptions"`:

- `IX_NormalizedDescriptions_CanonicalName_trgm` on `lower("CanonicalName") gin_trgm_ops`.
- `IX_NormalizedDescriptions_DisplayName_trgm` on `lower(COALESCE("DisplayLabel", "CanonicalName")) gin_trgm_ops`.

The catalog lexical channel now runs one relevance-ranked query whose filter is `LIKE '%token%'` on
exactly these two expressions. With the indexes, a large catalog answers that filter with a bitmap
index scan instead of a sequential scan. A small catalog still plans a sequential scan, which is
cheaper at that size. `pg_trgm` already exists, installed by `20260309124500_AddPgTrgmExtensionAndTrigramIndexes`.

The indexes are built inside the migration transaction, not `CONCURRENTLY`. While they build, writes
to the catalog table wait.

## Proven on a restored copy of production data

The migration was applied to a local instance restored from the owner's portable backup of
2026-10-29: 1,649 catalog rows, upgraded together with `20261031033213_AddCategoryRestructure`.

- The index statement took 66 ms, and each index is 360 kB.
- With `enable_seqscan = off`, a two-token lexical filter plans a `BitmapOr` of `Bitmap Index Scan`
  on both indexes (0.43 ms).
- At this size the default plan is a sequential scan (2.6 ms).

## Rolling back

`Down` drops both indexes. Retrieval still works without them, only more slowly on a large catalog.
