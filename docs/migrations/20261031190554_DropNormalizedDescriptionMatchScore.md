# Migration: DropNormalizedDescriptionMatchScore (RECEIPTS-1105)

**Migration id:** `20261031190554_DropNormalizedDescriptionMatchScore`
**Date:** 2026-10-31

Take a portable backup before deploying this migration.

## What it does

- Replaces `matching.guard_catalog_assignment()` with the same function minus its assignment to
  `NEW."NormalizedDescriptionMatchScore"`. plpgsql resolves columns only when a function runs, so
  without this every text edit of a mapped or excluded item would fail once the column is gone.
- Drops `receipts."ReceiptItems"."NormalizedDescriptionMatchScore"`, the similarity once recorded by
  the retired semantic resolver. Nothing reads it for a decision or display since RECEIPTS-1099.
- Drops `matching."NormalizationCatalogMembers"."PreviousScore"`, the catalog-undo snapshot of that
  score.
- Drops `matching."NormalizationAiProposals"."SourceMatchScore"`, which no code path wrote except a
  retry copy.

Backups keep format v15. Exports still write the `normalized_description_match_score` column on
`receipt_items` (always empty) because every v5+ importer selects it; imports ignore its values.

## Rolling back

`Down` re-adds the three columns as nullable and restores the earlier guard function. The dropped
scores are not restored; nothing reads them.
