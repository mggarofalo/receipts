# Migration: MakeReviewItemPreparedLinear (RECEIPTS-1112)

**Migration id:** `20261101121424_MakeReviewItemPreparedLinear`
**Date:** 2026-11-01

Take a portable backup before deploying this migration.

## What it does

Replaces `matching.review_item_prepared(uuid)`. The purchase review-evidence guard calls it once for
every written purchase.

| | Before | After |
|---|---|---|
| Where the prepared ids live | The transaction-local setting `receipts.review_prepared`, as one comma-joined string | A session temp table `review_prepared` with a primary key |
| What the setting holds | The ids themselves | The flag `on` |
| Cost of each check | A `position()` search through the whole string | One index probe |

The old version made one save quadratic in its size. Measured on 2 CPUs:

| Prepared items in one save | Before | After |
|---|---|---|
| 16,509 (owner backup) | 5.6 s | under 0.1 s |
| 49,527 (3×) | 51 s | 0.11 s |
| 99,054 (6×) | 231 s | 0.23 s |

`ApplicationDbContext.PrepareReviewEvidenceAsync` refills the temp table at each save, and the save
clears the flag afterwards. Ids left over from an earlier save are therefore never trusted.

The function trusts nothing unless the flag is exactly `on`. An older application that sets the joined
string sees every write treated as unprepared, so its evidence is downgraded to `NeedsReview` and
nothing is wrongly kept.

Backups keep their format.

## Rolling back

`Down` restores the string search. Roll the application back with it, because the old function
does not recognise the `on` flag.
