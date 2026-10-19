# React Best Practices

Project-specific React guidance for agents and contributors working in `src/client/`.

These rules are derived from the [official React docs](https://react.dev/learn) and tailored to the patterns, libraries, and conventions already established in this codebase. They supplement (not replace) the general frontend rules in [coding-standards.md](../coding-standards.md).

## Contents

| Guide | When to read |
|-------|-------------|
| [State Management](state-management.md) | Choosing where and how to store state |
| [Effects](effects.md) | Writing, auditing, or removing `useEffect` calls |
| [Component Patterns](component-patterns.md) | Building pages, forms, and reusable components |
| [Custom Hooks](custom-hooks.md) | Writing or modifying hooks in `src/client/src/hooks/` |

## Account and card selection

Transaction entry uses `useAccountCardSelection` with `AccountCardSelector` in both the receipt edit form and the new-receipt wizard. The form owns one controlled account/card pair. Account changes clear the card in the interaction callback; card options come from that account's query. The selector has no React Hook Form dependency, so the planned receipts-filter cascade (RECEIPTS-849) can reuse this boundary when implemented.

The hook loads every account page, offers active accounts/cards for new choices, and retains an inactive current account or card for historical edits. Loading and initial lookup errors preserve the current IDs. A successful scoped response that excludes the selected card establishes an invalid pair; that remains invalid if a later refetch fails.

Every submit consumer must call the hook's `validate(values)` before submitting or adding a row. It reads the latest successful scoped cache data, including data received during asynchronous form validation. Rendering an error or filtering dropdown options is not sufficient to enforce the pair. Keep required-card validation in both create and edit forms. Do not silently rewrite user selections from an Effect.

After RECEIPTS-852, the account is selection/display state in these forms. Transaction write requests send `cardId`, amount and date; the server derives account ownership from the card. The shared card-change invalidation contract refreshes both scoped choices and affected transaction views. A zero-card account offers an explanation and a link to the existing card-creation flow; it does not imply that flow automatically preselects the account.

## Receipt picker

`ReceiptPicker` is the receipt-item form's domain-specific paginated combobox. Keep server pagination, debounced Location search, loaded-option date matching and selected-detail hydration in this boundary; the general `Combobox` must remain free of receipt query state. The trigger forwards its native props and ref so `FormControl` can supply its accessible name and validation relationships.

The picker loads only while open. Near-bottom scroll and the explicit load-more button request one page at a time. Search results use keys distinct from unfiltered browse pages, and an unloaded date match is never inferred from the Location-only API. The selected receipt detail is keyed independently and remains available when search changes or the picker closes. Failure owners sit outside command filtering so the typed search cannot hide retry.

## Normalization workflows

See [Normalization and receipt draft interactions](normalization-workflows.md) for dialog lifetimes, verified selections, simulation evidence and shared draft validation.

## Browse-table controls

Reuse `SortableTableHead` and `Pagination` for visible sorting and page controls.
Keep data ownership explicit: `useServerPagination` describes a server request;
`useLocalTable` accepts a **complete** cached collection and memoizes filtering
and stable sorting before slicing. Never give the local hook one server page or
claim that filtering downloaded rows searches the full dataset. Keep filter and
comparison callbacks stable so a page change does not repeat the projection.

Every browse table supplies a stable, distinct `tableId`. Page sizes (10, 25,
50, 100) live under `table-page-size:<tableId>` in local storage; the old global
setting is a read-only fallback until that table gets its own preference. Storage
failures must not prevent browsing. Purchase history and paginated reports also
accept explicit URL page sizes; those override the preference for that view.
Changing page size, sort, or applied filters resets pagination. Report CSV exports
retain their own fetch-all batching and never inherit the displayed page size.

Purchase-history filters are form drafts until Apply succeeds; invalid or partial
ranges must not send queries. Applied search, filters, sort, and pagination live
in the URL and in the React Query key, and requests retain AbortSignal support.
The server applies all filters and ordering before counting and paging. API keys
demonstrate the complete-cache alternative with the same presentation controls.
Editable receipt rows and chart accessibility tables are not browse datasets and
do not gain pagination merely because they render table markup.
