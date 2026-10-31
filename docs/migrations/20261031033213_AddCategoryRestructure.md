# Migration: AddCategoryRestructure (RECEIPTS-1084)

**Migration id:** `20261031033213_AddCategoryRestructure`
**Date:** 2026-10-31

Take a portable backup before deploying this migration.

## What it does

- Adds three tables in the `taxonomy` schema for [category restructure plans](../restructure-plans.md):
  - `RestructureApplications`: one row per applied plan, keyed by its historical operation, with the
    plan, its hash, its dry-run report and the base, applied and undo category revisions.
  - `RestructureItems`: the rule, evidence and occasion change for each captured purchase.
  - `RestructureChanges`: a ledger of the default envelopes, item and receipt envelopes, envelope
    archives, occasions and approved mappings that an application changed, so undo can put each
    back.
- Allows the historical operation kind `RestructureV1`. A restructure moves purchases through the
  existing chunked apply and conditional undo. The header identity check
  (`historical_capture_mapping_identity`) is replaced and stays `NOT VALID`, as before. A
  restructure header carries no mapping revision. The item guard (`guard_historical_provenance_item`)
  rejects mapping provenance on restructure items, as it does on manual ones.

No existing row is rewritten. The new tables start empty.

## Rolling back

`Down` takes an exclusive lock on the historical tables. It refuses (SQLSTATE 23514, "Category
restructure history is retained") while any `RestructureV1` operation exists, because the earlier
schema cannot represent it. Otherwise it restores the previous constraints and item guard, and
drops the three tables.
