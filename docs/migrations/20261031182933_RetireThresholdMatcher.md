# Migration: RetireThresholdMatcher (RECEIPTS-1099)

**Migration id:** `20261031182933_RetireThresholdMatcher`
**Date:** 2026-10-31

Take a portable backup before deploying this migration.

## What it does

- Drops `matching."NormalizedDescriptionSettings"`, the singleton row that held the retired
  matcher's auto-accept and pending-review thresholds. No live code path read them; see
  [the decision](../decisions/2026-10-31-retire-threshold-matcher.md).
- Drops `IX_ReceiptItems_NormalizedDescriptionMatchScore` on `receipts."ReceiptItems"`. It served
  only the retired threshold-impact preview. The `NormalizedDescriptionMatchScore` column stays.

No receipt data is rewritten. Backups keep the same format version: exports write an empty
`normalized_description_settings` table, and imports ignore rows in it.

## Rolling back

`Down` recreates the table seeded with the original defaults (0.81 auto-accept, 0.68 pending
review) and recreates the index. Thresholds an administrator had changed before the upgrade are
not restored; the previous release only used them to simulate matches.
