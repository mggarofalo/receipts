# Retire the threshold matcher

Date: 2026-10-31. Status: accepted. Tracking: RECEIPTS-1099.

## Context

The Normalized Descriptions **Settings** tab held a matcher: auto-accept and pending-review
thresholds, "Preview impact" and "Test a Description". It simulated a semantic auto-link resolver
that no longer runs; its own copy said the bands "only simulate classifier outcomes". Live
resolution uses reviewed exact scoped mappings only (`NormalizationCatalogResolver`), which never
calls a model or reads thresholds.

## Decision

The owner's rule: re-point "test match" only if an independent assessment found it adds value;
otherwise retire it. The goal is a better UX, not more complexity.

The assessment found no value, so the matcher is retired. Catalog source review
(`CatalogSourceReview.tsx`) already shows the real `NormalizationCandidateRetrieval` shortlist,
with a free-text "Find existing name", "Found by" evidence and previously approved descriptions.
The receipt-entry chooser shows the filtered set users actually get. A re-pointed match test would
be a third copy of the same search. There is no replacement UI.

## Consequences

- The settings, settings-preview and test endpoints, their schemas and the Settings tab are
  removed. `?tab=settings` falls back to the Review Queue.
- `matching.NormalizedDescriptionSettings` and the index on
  `ReceiptItems.NormalizedDescriptionMatchScore` are dropped
  ([migration](../migrations/20261031152711_RetireThresholdMatcher.md)).
- Merge and split clear the moved items' match score instead of measuring it again against the
  surviving row, so they no longer need an embedding.
- Backups keep the format version. Exports write an empty `normalized_description_settings` table,
  so the archive stays a valid v15 file for any importer that reads v15 (each one requires the
  table). Imports ignore any rows it holds. Release 0.17.1 reads only formats 1-13, so it cannot
  restore any v15 export, with or without this change.
- The `NormalizedDescriptionMatchScore` column stays; dropping it is a separate item.
