# Design

## Context

See proposal.md (Why). The rule itself, and the rejected alternatives (freeze at first push; a provisional `suggested` source), are recorded in `docs/decisions/2026-11-15-envelope-fixed-once-recorded.md` and are not reopened here.

Current state:
- **One derivation policy.** `EnvelopeDerivationPolicy` (Application) decides every envelope outcome.
- **One write chokepoint.** `ItemEnvelopeWriter` (Infrastructure) applies it at each write. Every classification write goes through `ClassificationService.AppendAsync`, which calls `AfterItemTypeChangeAsync`.
- **Restructure already skips the hook.** It appends through a plain `Append` that never calls it, so restructure needs no change.
- **Item choices.** `PUT …/budget-choices` reaches `ApplyItemChoicesAsync` through `SetReceiptItemBudgetChoicesCommand` and `BudgetChoiceService`. Today a null envelope re-derives only when the stored source is `item`.
- **Split comparison preview.** The comparison's `CurrentHierarchy` branch uses `YnabReceiptMappingResolver.ResolveCurrent`.
- **YNAB status.** The receipt page already knows each receipt's status (`notSynced | pending | synced | failed | unknown`).

## Goals / Non-Goals

**Goals:**
- One derivation policy stays the only place envelopes are derived. The new action reuses it.
- Removing the old meanings leaves nothing dead behind: no unused resolver, enum value or client branch.

**Non-Goals:**
- Repairing existing data. None has drifted.
- Server-side enforcement of the "never pushed" condition. It is a client affordance; the server accepts Automatic from a person on any receipt.

## Decisions

### Policy (Application: `EnvelopeDerivationPolicy`)
- **Fill-only type changes.** Replace `FollowsItemType` with `IsUnresolved(stored) => stored.EnvelopeId is null`. `AfterItemTypeChange(stored, default)` returns `(default, Default)` only when the stored envelope is unresolved and the default is non-null; otherwise it returns `stored`. That one rule gives "fill only", "a null default keeps the stored source", and "clearing a type keeps the envelope".
- **`Automatic(paidFrom, typeDefault)`** returns `ForNewItem(null, paidFrom, typeDefault)`, and its caller refuses when the result is unresolved.
- **`AfterOverrideChange`** becomes a plain override setter. A non-null value becomes an `item` override; a null value keeps `stored`. Returning to automatic is no longer its job.
- *Alternative:* keep `FollowsItemType` and add a flag. Rejected: once nothing follows the type, the name would misdescribe the behaviour.

### Automatic action (Presentation → Application → Infrastructure)
- **New route:** `POST /api/receipt-items/{id}/envelope/automatic` on `ReceiptItemsController`, with the same authorization as the budget-choices route. It returns `Results<NoContent, NotFound, ConflictProblem>`. The 409 `detail` is "No envelope can be derived: the receipt has no paid-from and the item type has no default."
- **New command:** `SetReceiptItemEnvelopeAutomaticCommand` in Application, handled through a new `IBudgetChoiceService.SetItemAutomaticAsync`. The service returns a result that distinguishes not-found, applied and unresolvable, so the controller can map each one without exceptions.
- **Writer:** a new `ItemEnvelopeWriter.ApplyAutomaticAsync` resolves the active paid-from and the current type's default the same way `ApplyItemChoicesAsync` does today, so that code moves rather than gets copied. It then calls `Automatic`.
- **Why a separate action rather than a flag on the PUT:** Automatic is an action, not a stored choice, and the PUT replaces stored choices. A flag would allow contradictory bodies, such as an automatic flag sent together with an `envelopeId`.

### PUT budget-choices meaning (BREAKING in meaning)
- `ApplyItemChoicesAsync` records an override when `envelopeId` is non-null and otherwise keeps the envelope. The early return for non-`item` sources goes away, because it now covers every source.
- The OpenAPI description of `envelopeId` changes from "Null is automatic" to "Null keeps the recorded envelope; use the Automatic action to re-derive". The schema shape is unchanged.

### Split comparison (BREAKING in schema)
- **Server:** remove the `categoryView` query parameter and response field, `YnabComparisonCategoryView`, the `CurrentHierarchy` branch, and `YnabReceiptMappingResolver.ResolveCurrent`, if nothing else uses it. `ExternalCategoryMappingPolicy.ResolveCurrent` stays: the backfill planner uses it.
- **Client:** remove the selector and its copy from `YnabSplitComparisonContent`, the `categoryView` argument of `useYnabSplitComparison`, and any query-key part or invalidation entry that carries it.

### Client
- **Hook:** add `useSetReceiptItemEnvelopeAutomatic` to `useBudgetChoices.ts`. On a 409, it shows the problem `detail` via `extractErrorMessage`. It invalidates the same queries as the existing item-choices mutation.
- **Envelope menu:** the **Automatic** entry in `EnvelopeOverrideMenu` (inside `ReceiptItemsCard`) calls the new hook, and is offered for every source, not only `item`. Choosing an envelope still uses the PUT.
- **Move offer:** after `handleUpdate` saves a type change, offer "Also move to X?" when all of these hold:
  - the receipt's YNAB status is `notSynced`;
  - the item's source is `default` or `historical`;
  - the existing preview helper in `lib/envelope-display.ts` gives a non-null X for the new type;
  - X differs from the recorded envelope.

  Accepting calls the Automatic hook. X always matches what Automatic records, because a `default` or `historical` source implies the receipt has no active paid-from.
- **Warnings:** before an override, Automatic or paid-from change, a confirmation dialog warns that YNAB will not be updated when the status is anything other than `notSynced`.
- **YNAB status prop:** `ReceiptDetail` passes `persistedYnabStatus` to `ReceiptItemsCard` as a prop. A missing status counts as not `notSynced`, so the code is cautious.
- **Rules:** follow `docs/react/`. Derive the offer's conditions during render, not in an Effect; the handler drives the dialog.

### Docs
- **`docs/envelopes.md`:** rewrite the item-type-change bullet as fill-only and remove the "current behaviour" pointer added by RECEIPTS-1118. Describe Automatic and the null-keeps rule. Remove the "current hierarchy" split preview paragraph. Make the `EnvelopeSource` table describe provenance only.
- **Other docs:** one line each in `docs/historical-reclassification.md`, `docs/approved-category-mappings.md` and `docs/jev-suggestion-adapter.md`, saying they only fill envelopes that have none.
- **ADR:** mark RECEIPTS-1124 as implemented in the 2026-11-15 envelope ADR.

## Risks / Trade-offs

- **[The meaning change on the PUT is invisible to the breaking-change gate.]** → The PR title carries `!`, the spec delta records it, and a controller test pins "null keeps an override".
- **[The client's offer could fire when Automatic would record something else.]** → The equivalence above holds by construction. A client test covers the condition table, and the server stays the source of truth.
- **[Clearing a receipt's paid-from when the type has no default still leaves receipt-sourced items unresolved.]** → That is an explicit person action the spec allows, and the warning covers pushed receipts. Unchanged.
- **[Removing the preview breaks any external caller of `categoryView`.]** → The client is the only caller in this repo. The label and the `!` mark the break.

## Migration Plan

There is no schema or data change, so there is no migration, rehearsal or `docs/migrations` note. Rollback is a revert. Envelopes written under the new rule are valid under the old one.
