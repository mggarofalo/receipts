# Proposal

Plane: RECEIPTS-1118.

## Why

Item classification was rebuilt over RECEIPTS-1044…1099. Its intended behaviour is spread across about sixteen docs that were written one story at a time. Some of them contradict each other, and some contradict the code. The owner is now testing classification by hand and needs one answer key. That means specs that state what the system is meant to do, so a failed test can be called either a bug or a spec change.

## What Changes

- **Seeds the first OpenSpec specs.** There are nine capabilities: eight for classification and one for envelope attribution. Every requirement is ADDED.
- **Specs state intended behaviour, not current behaviour.** The owner has answered the open questions where the docs and code disagree, and the specs follow those answers:
  - the tree is limited to two levels;
  - definitions are optional in the editor;
  - stored legacy labels and restructure clears no longer block automation;
  - historical cleanup is Admin-only;
  - Top category follows the dashboard's view;
  - automatic Jev writes are intended.
- **Records a new envelope rule decided in this planning.** An envelope is fixed once recorded. A classification change may only fill an envelope that has none. Receipts never rewrites history already in YNAB. Today's behaviour, where a type change re-derives `default`-sourced envelopes, is not intended.
- **Adds two ADRs under `docs/decisions/`:**
  - Jev strong-match automatic writes supersede the automatic-action gate in the 2026-10-28 ADR.
  - Envelopes are fixed once recorded, and YNAB history is not rewritten.
- **Fixes doc lines that later decisions superseded**, so the docs agree with the specs.
- **Out of scope:**
  - Any code, OpenAPI or schema change.
  - The code fixes for the gaps listed under Impact. Each is its own Plane issue.
  - Envelope implementation work (the fill-only hook, the **Automatic** action, warnings on pushed receipts, removing the split comparison's current-hierarchy option). That is a separate change.
  - A targeted correction of a single YNAB transaction.
  - Specs for portable backup, normalization and legacy migration.

`openapi/spec.yaml` does not change. The API contract is unaffected. No EF migration or data backfill.

## Capabilities

### New Capabilities

- `category-taxonomy`: the single versioned tree of item types, its immutable revisions, its validation (including the two-level limit) and the Admin editor.
- `purchase-classification`: the append-only assignment record for each receipt item and its sources. Also the rules every writer shares (automation eligibility and conditional correction), the envelope boundary, and the Uncategorized items list.
- `category-reporting`: spending by item type in the "as classified" and "current hierarchy" views, with knowledge cutoff, ID-keyed buckets and the states for null and unresolved items. Reporting never writes.
- `approved-category-mappings`: Admin-approved exact-source mappings, what keeps or breaks an approval, reuse at save, and the bounded background worker.
- `automatic-classification-activity`: the list of committed automatic writes (from mappings and Jev), attributed to the responsible person, with conditional undo.
- `category-assistance`: opt-in Jev assistance, including what is sent, strong-match automatic writes, suggestions and abstentions, review preferences, limits, settings-revision staleness, and Jev's answers in the Uncategorized items view.
- `historical-reclassification`: Admin cleanup campaigns over exact source groups, with a frozen preview, chunked apply, cancel and conditional undo.
- `category-restructure`: one reviewed, undoable Admin step that rebuilds the tree and moves past purchases.
- `envelope-attribution`: how an item's envelope is first recorded, fixed afterwards, and changed only by a person, so YNAB history is preserved.

### Modified Capabilities

None. `openspec/specs/` is empty.

## Impact

- **Files added:** nine spec files under this change, and two ADRs in `docs/decisions/`.
- **Docs edited:**
  - `docs/classification-contract.md`
  - `docs/taxonomy-revisions.md`
  - `docs/historical-reclassification.md`
  - `docs/approved-category-mappings.md`
  - `docs/category-reporting-foundation.md`
  - `docs/envelopes.md` (a stale line, plus a pointer to the new spec)
  - the 2026-10-25 and 2026-10-28 ADRs (status and superseded lines only)
- **Known code gaps.** The specs knowingly state behaviour the code does not yet have. Manual tests of these will fail until the follow-up issues land:
  - no depth limit in the editor;
  - legacy labels block automation;
  - restructure-cleared items are skipped;
  - cleanup endpoints only require sign-in;
  - the dashboard's Top category ignores the view;
  - type changes re-derive `default` envelopes, and clearing a type wipes the envelope.
