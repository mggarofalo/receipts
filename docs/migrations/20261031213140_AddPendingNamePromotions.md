# Migration: AddPendingNamePromotions (RECEIPTS-1107)

**Migration id:** `20261031213140_AddPendingNamePromotions`
**Date:** 2026-10-31

This migration adds two empty tables. It reads and rewrites no existing row.

## What it does

- `matching."PendingNamePromotions"`: one row per bulk promotion of Pending review names, with the
  actor, the time, the purchase count and the undo state.
- `matching."PendingNamePromotionEntries"`: one row per promoted name, with its revision just after
  the promotion, so undo can tell whether the name has changed since. There is deliberately no
  foreign key to the name, so the history survives a merge or a deletion.

Promotion itself is an administrator action, not part of the migration. See
[the decision](../decisions/2026-10-31-promote-consistent-pending-names.md). Both tables are an
operation log, so portable backups leave them out.

## Proven on a restored copy of production data

The migration ran on a local instance restored from the owner's portable backup of 2026-10-29.

- **Apply:** applied with DbMigrator to a clone of that instance.
- **Timing:** the migration's SQL took about 12 ms in a transaction that was rolled back.
- **Promotion on the clone:** promoted 816 names covering 11,584 purchases in 6.5 s. Every linked
  purchase kept its review state.
- **Eligibility query:** runs in 75 ms on that data.

## Rolling back

`Down` drops both tables. Names that a promotion made Active stay Active. To return them to review
first, undo the promotion before rolling back.
