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
