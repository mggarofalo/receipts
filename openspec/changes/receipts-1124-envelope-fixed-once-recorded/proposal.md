# Proposal

Plane: RECEIPTS-1124.

## Why

The `envelope-attribution` spec and the 2026-11-15 ADR say an envelope is fixed once recorded, and that a classification change only fills an envelope that has none. The code still does the old thing:
- Every type change re-derives envelopes that have source `default`.
- Clearing a type wipes the envelope.
- "Automatic" works only for per-item overrides.
- Nothing warns before a local change that YNAB will not follow.

That lets bulk re-typing move envelopes on receipts already pushed to YNAB, which is the drift the rule exists to prevent. Nothing has drifted yet, because the app has not been used since envelopes shipped. So the code should be fixed before it is used again.

## What Changes

- **Type changes only fill.** A classification change, by any path, fills an item's envelope only when the item has none. A fill that resolves to nothing keeps the stored source. Clearing a type keeps the envelope.
- **New `POST /api/receipt-items/{id}/envelope/automatic`.** It re-derives the item's envelope once, for any source: the receipt's paid-from, else the item type's default. When nothing resolves, it refuses with 409 and a problem `detail`. It becomes the only way to return an item to automatic.
- **BREAKING:** on `PUT /api/receipt-items/{id}/budget-choices`, `envelopeId: null` now keeps the recorded envelope instead of removing an override. The schema is unchanged; only the meaning changes, so the API Breaking Changes gate will not detect it.
- **BREAKING:** `GET /api/ynab/receipts/{receiptId}/split-comparison` loses its `categoryView` query parameter and response field. The comparison always uses recorded envelopes. The "Preview current hierarchy" option no longer predicts anything, because saving no longer moves envelopes.
- **Client:**
  - The envelope menu's **Automatic** calls the new endpoint.
  - After a person changes an item's type on a receipt never pushed to YNAB, the client offers "Also move to X?".
  - On any receipt whose YNAB status is not `notSynced`, the client warns before an override, Automatic or paid-from change.
  - The split comparison drops its view selector.
- **Docs:** `docs/envelopes.md` and the `EnvelopeSource` descriptions become provenance only.
- **Out of scope:**
  - Repairing envelopes that already drifted. None have.
  - A bulk drift report (RECEIPTS-1127).
  - Targeted correction of a single YNAB transaction (RECEIPTS-1126).
  - Warnings on re-linking an envelope to another YNAB category, or on edits made after a push.

`openapi/spec.yaml` changes in two incompatible ways and one compatible way (the new endpoint). The PR needs the `breaking-changes-allowed` label and `!` in its title. There is no EF migration and no data backfill.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `envelope-attribution`:
  - The rule on who may change a recorded envelope now names the Automatic endpoint.
  - New requirements say that a null envelope on budget-choices keeps the envelope, and define the Automatic endpoint's contract.
  - A new requirement says the split comparison uses recorded envelopes only.

## Impact

- **Application:** the envelope derivation policy and the YNAB split comparison query, which drops the current-hierarchy branch and the resolver it used.
- **Infrastructure:** the item envelope writer, including its type-change hook and item choices, and the budget-choice service.
- **Presentation:** the receipt items controller (new action) and the YNAB split comparison endpoint.
- **API:** `openapi/spec.yaml` and the regenerated `api.d.ts`.
- **Client:**
  - `useBudgetChoices` (a new Automatic mutation);
  - `ReceiptItemsCard` (the offer, the warnings and the menu);
  - `ReceiptDetail` (passes the YNAB status down);
  - `BudgetChoices` copy;
  - `YnabSplitComparisonContent` and `useYnab` (selector removed).
- **Tests:** existing envelope policy, writer, classification, assistance and resolver tests that encode "follows the type" are rewritten.
- **Docs:** `docs/envelopes.md`, `docs/historical-reclassification.md`, `docs/approved-category-mappings.md` and `docs/jev-suggestion-adapter.md`.
