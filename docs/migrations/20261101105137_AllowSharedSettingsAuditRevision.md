# Migration: AllowSharedSettingsAuditRevision (RECEIPTS-1085)

**Migration id:** `20261101105137_AllowSharedSettingsAuditRevision`
**Date:** 2026-11-01

Take a portable backup before deploying this migration.

## What it does

- Recreates `category_assistance."SettingsAudit"."IX_SettingsAudit_Revision"` without uniqueness.

A category assistance settings save now starts a new settings revision only when consent, provider,
model or automatic writes change. A save that changes only the daily or pending limit keeps the
revision, so outstanding suggestions stay decidable, and its audit row records the same revision as
the previous one. See [category assistance](../category-assistance.md).

No data changes. The table is small (one row per settings save), so the rebuild is instant.

## Rolling back

`Down` restores the unique index. It refuses (`23514`) while any two audit rows share a revision,
which happens after the first limits-only save, because the unique index cannot hold them. The
audit is append-only, so those rows cannot be removed to make room.

To roll back after that, revert the code only and leave this migration applied. The earlier code
runs unchanged on the non-unique index: it rotates the revision on every save, so its own audit
rows stay distinct.
