# Tasks

## 1. Refactor (behaviour unchanged)

- [x] 1.1 Extract the "active paid-from, else the current type's default" lookup in `ItemEnvelopeWriter.ApplyItemChoicesAsync` into one private helper that both the existing path and the coming Automatic path can call. Verify that `dotnet test Receipts.slnx --filter "Category!=Integration"` stays green with no test changes.

## 2. Fill-only type changes

- [x] 2.1 Add failing `EnvelopeDerivationPolicyTests` cases, then make them pass by replacing `FollowsItemType` with `IsUnresolved` and making `AfterItemTypeChange` fill-only. The cases:
  - a recorded envelope (any source) is kept;
  - an unresolved envelope is filled with source `default`;
  - a null default keeps the stored source, so (null, `historical`) stays `historical`;
  - clearing a type keeps the envelope.

  Verify the policy tests pass.
- [x] 2.2 Update `ItemEnvelopeWriter.AfterItemTypeChangeAsync` and its comment, plus the hook comment in `ClassificationService`. Rewrite the tests that encode "follows the type":
  - `ItemEnvelopeWriterTests.Reclassify_DefaultSourcedItem_FollowsNewItemTypeDefault`;
  - `ClassificationTests.Envelopes`;
  - `CategoryAssistanceAutoWriterTests.Envelopes`;
  - `ReceiptRepositoryUpdateLockTests`, where affected.

  Add writer tests for clearing to unclassified keeping the envelope, and for a historical reclassification apply and undo leaving recorded envelopes unchanged. Verify unit tests pass, and the Postgres gate (`dotnet run scripts/run-prerequisite-tests.cs -- postgres`) passes.

## 3. OpenAPI contract

- [x] 3.1 In `openapi/spec.yaml`, make three edits:
  - add `POST /api/receipt-items/{id}/envelope/automatic`, with 204, 404, and 409 as `application/problem+json` `ProblemDetails`;
  - change the `ReceiptItemBudgetChoicesRequest.envelopeId` description to "null keeps";
  - remove `categoryView` from the split-comparison parameter and response, along with its 400 "Invalid category view".

  Rewrite the `EnvelopeSource` description as provenance only. Regenerate types with `(cd src/client && npm run generate:types:write)`. Verify `npm run lint:spec` passes, and that `npm run check:drift` passes after section 4.

## 4. Automatic action

- [x] 4.1 Add `EnvelopeDerivationPolicy.Automatic` with policy tests: paid-from gives `receipt`, else the default gives `default`, and an unresolved result is reported, not recorded. Verify the policy tests pass.
- [x] 4.2 Add these, each with handler and service tests:
  - `SetReceiptItemEnvelopeAutomaticCommand` and its handler;
  - `IBudgetChoiceService.SetItemAutomaticAsync`, which returns not-found, applied or unresolvable;
  - `ItemEnvelopeWriter.ApplyAutomaticAsync`, using the helper from 1.1.

  The tests cover an override returning to the default, a `default` item taking the paid-from, the occasion staying unchanged, and the refusal when nothing resolves. Verify the tests pass.
- [x] 4.3 Add the controller action on `ReceiptItemsController`, returning `Results<NoContent, NotFound, ConflictProblem>` with an `ApiProblem.Conflict` `detail`, and receipt-write authorization. Add controller tests for 204, 404, 409 and 403. Verify the tests pass and `dotnet run scripts/generate-api-contract.cs` followed by `npm run check:drift` reports no drift.

## 5. PUT budget-choices: null keeps

- [x] 5.1 Change `ItemEnvelopeWriter.ApplyItemChoicesAsync` and `EnvelopeDerivationPolicy.AfterOverrideChange` so that a non-null envelope records an override and null keeps the envelope, for every source. Add tests for an occasion-only edit keeping the envelope, and for null keeping an `item` override. Update the existing return-to-automatic tests to use the Automatic action. Verify unit tests pass.

## 6. Split comparison: recorded envelopes only

- [x] 6.1 Remove these, and delete their tests in `YnabReceiptMappingResolverTests`:
  - `YnabComparisonCategoryView`;
  - the `CurrentHierarchy` branch in `GetReceiptYnabSplitComparisonQueryHandler`;
  - `YnabReceiptMappingResolver.ResolveCurrent`;
  - the controller's `categoryView` binding.

  Keep `ExternalCategoryMappingPolicy.ResolveCurrent`. Add a handler test: an item whose recorded envelope differs from today's default is placed under the recorded envelope's YNAB category. Verify that a build with warnings as errors succeeds and the tests pass.

## 7. Client

- [x] 7.1 Add `useSetReceiptItemEnvelopeAutomatic` to `src/client/src/hooks/useBudgetChoices.ts`, using the same invalidation as the item-choices mutation and showing a 409 `detail` via `extractErrorMessage`. Add hook tests for success, 409 and the invalidation. Verify `npx vitest run src/hooks/useBudgetChoices` passes.
- [x] 7.2 In `ReceiptItemsCard`, make the envelope menu's **Automatic** call the new hook for every source; choosing an envelope still uses the PUT. Pass `persistedYnabStatus` from `ReceiptDetail` as a prop. Update `ReceiptItemsCard` tests. Verify the tests pass.
- [x] 7.3 Add the "Also move to X?" offer after a type change. Its conditions are a `notSynced` status, a `default` or `historical` source, a non-null X from `lib/envelope-display.ts`, and X differing from the recorded envelope. Derive the conditions during render; accepting calls Automatic. Add tests for when the offer is shown and when it is suppressed (status, source, null X, equal X), and for accept and decline. Verify the tests pass and `react-render-qa` reports nothing.
- [x] 7.4 Add the confirmation warning before an override, Automatic or paid-from change when the status is not `notSynced` (including a missing status). Update the copy in `BudgetChoices.tsx`. Add tests for the warning shown or skipped and for confirm and cancel. Verify the tests pass.
- [x] 7.5 Remove the split comparison's view selector, its copy, and the `categoryView` argument and query-key part from `YnabSplitComparisonContent` and `useYnab`, along with any `query-invalidation` entry. Update their tests. Verify the client test suite, `npx tsc --noEmit` and eslint all pass.

## 8. Docs

- [x] 8.1 Rewrite `docs/envelopes.md`:
  - the item-type-change bullet becomes fill-only, and the "current behaviour" pointer goes;
  - describe Automatic and the null-keeps rule, and remove the "current hierarchy" split preview paragraph;
  - make the `EnvelopeSource` table describe provenance only.

  Add one line each to `docs/historical-reclassification.md`, `docs/approved-category-mappings.md` and `docs/jev-suggestion-adapter.md`. Mark RECEIPTS-1124 as implemented in `docs/decisions/2026-11-15-envelope-fixed-once-recorded.md`. Verify `grep -n "follow" docs/envelopes.md` finds no claim that an envelope follows its type.

## 9. Verification and release marking

- [ ] 9.1 Run `openspec validate receipts-1124-envelope-fixed-once-recorded --strict`, the full unit test step, the client tests and the Postgres gate, and confirm all pass. Then exercise the receipt page through the `run` skill: menu Automatic, the move offer on an unpushed receipt, the warning on a pushed one, and the split comparison without its selector.
- [ ] 9.2 Open the PR with a `!` title, for example `feat(api,client)!: fix envelopes once recorded`, and apply the `breaking-changes-allowed` label. The description lists both breaks: the meaning of a null `envelopeId` on the PUT, and the split comparison's removed `categoryView`. Verify the label is present and the PR title check passes.

## Workflow follow-up

- Review the PR (find-bugs), then archive this change on the branch as the last commit before squash-merge.
- After merge, confirm main's CI is green, and move RECEIPTS-1124 to Done.
