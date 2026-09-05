# State Management

Rules for choosing where and how to store state in this project.

## The State Hierarchy

This project uses four tiers of state. Pick the lowest tier that satisfies the requirement.

| Tier | Tool | When to use | Example |
|------|------|-------------|---------|
| **Derived** | `const x = ...` or `useMemo` | Value can be calculated from existing state/props | `fullName`, filtered lists, `highlightMissing` |
| **Local** | `useState` | UI-only state scoped to one component | Dialog open/closed, selected rows, filter values |
| **Shared client** | Context (`useAuth`, `useShortcuts`) | Client state needed by many distant components | Auth token, keyboard shortcut help dialog |
| **Server** | TanStack Query (`useReceipts`, etc.) | Data that lives on the server | Entity lists, dashboard metrics, audit logs |

### Derive First

Before reaching for `useState`, ask: *"Can I calculate this from state/props I already have?"*

```tsx
// BAD — redundant state
const [fullName, setFullName] = useState('');
useEffect(() => {
  setFullName(firstName + ' ' + lastName);
}, [firstName, lastName]);

// GOOD — derived during render
const fullName = firstName + ' ' + lastName;

// GOOD — memoized if expensive (rare)
const filteredResults = useMemo(
  () => applyFilters(results, FILTER_DEFS, filterValues),
  [results, filterValues],
);
```

### Never Mirror Props into State

```tsx
// BAD — drifts out of sync when parent re-renders
function Message({ messageColor }) {
  const [color, setColor] = useState(messageColor);
}

// GOOD — use the prop directly
function Message({ messageColor }) {
  const color = messageColor;
}
```

Exception: `useState(initialX)` where the prop name signals "initial" and the component intentionally takes ownership. Name the prop `initialX` or `defaultX` to make this explicit.

## State Structure Rules

### 1. Group related values

If two state variables always update together, merge them.

```tsx
// BAD
const [x, setX] = useState(0);
const [y, setY] = useState(0);

// GOOD
const [position, setPosition] = useState({ x: 0, y: 0 });
```

### 2. Eliminate contradictory states

Replace multiple booleans with a single status enum.

```tsx
// BAD — isSending && isSent is possible
const [isSending, setIsSending] = useState(false);
const [isSent, setIsSent] = useState(false);

// GOOD
const [status, setStatus] = useState<'typing' | 'sending' | 'sent'>('typing');
const isSending = status === 'sending';
```

### 3. Store IDs, not duplicates

When state references an item from a list, store the ID and derive the object.

```tsx
// BAD — selectedItem is a copy that drifts when items updates
const [selectedItem, setSelectedItem] = useState(items[0]);

// GOOD
const [selectedId, setSelectedId] = useState<string | null>(null);
const selectedItem = items.find(item => item.id === selectedId) ?? null;
```

### 4. Keep state flat

Avoid deeply nested objects. Prefer flat structures with IDs referencing related data — the same principle behind the `accountMap` / `receiptMap` patterns in `Transactions.tsx`.

## When to Use `useReducer`

Use `useReducer` over `useState` when:

- Multiple state variables change together in response to one event
- The next state depends on the previous state in non-trivial ways
- State transitions have complex business rules

This project already uses `useReducer` for the receipt wizard (`wizardReducer.ts`). The `useServerPagination` hook also uses a reducer internally.

**Reducer rules:**

- Reducers must be pure — no side effects, no API calls
- Each action describes a user interaction, not an implementation detail
- Throw on unknown action types (catches typos at dev time)

## Server State (TanStack Query)

All API data flows through TanStack Query hooks in `src/client/src/hooks/`. Never fetch data with `useEffect` + `useState` — use the established hook patterns.

**Conventions:**

- Query keys: `["entity", "list", offset, limit, sortBy, sortDirection]` for lists, `["entity", id]` for singles
- Mutations show success/error toasts in `onSuccess`/`onError`
- Optimistic updates use `onMutate` with rollback in `onError` (see `useDeleteReceipts`)
- Composite mutations invalidate all affected query families (see `useCreateCompleteReceipt`)

### Authenticated session lifetime

`AuthProvider` owns the `QueryClientProvider`. Login, logout, failed refresh, and external credential replacement establish a new session generation: abort the old session's requests, cancel and clear its cache, create a fresh query client, and remount the authenticated subtree. Ordinary refresh-token rotation keeps the current generation. Do not add a second application-wide query client outside this boundary.

Use `useSessionMutation` for authenticated mutations. It preserves TanStack Query's mutation API while preventing obsolete work from dispatching with replacement credentials or running completion callbacks in the new session. Keep downloads, toasts, navigation, and other completion effects in guarded callbacks; a `mutationFn` should perform the operation and return its result. If a mutation makes several requests, capture its starting generation and check it before each subsequent request after an asynchronous gap. Optimistic updates must use the query client captured by that hook.

Code awaiting `mutateAsync` can still receive cancellation. Suppress `isAbortError(error)` in caller-owned error handlers. Before caller-owned success effects after an asynchronous gap, use `assertSessionCurrent` with the generation captured when the operation started. Raw authenticated `fetch` calls must include `getSessionSignal()` alongside their caller signal. Cancellation prevents stale client effects; it cannot undo a write already accepted by the server.

Private user queries include the user ID in their keys (API keys and personal authentication history). Physical cache replacement also isolates shared receipt data and optimistic writes. `useSignalR` follows the same generation, stops obsolete connections, and discards their callbacks and buffered toasts.

Server logout conditionally revokes the refresh-token family captured in the signed JWT. A delayed logout cannot revoke a later login's family. This preserves the deliberate policy that an already-issued access JWT remains valid until expiry unless a separate security-stamp operation revokes it; local session cleanup is immediate.

Session regressions should compose the real `AuthProvider`, query client, hooks, and transport. Mocking the provider or mutation wrapper cannot demonstrate isolation across account changes.

## Context

The project has two contexts: `AuthContext` and `ShortcutsContext`. Before creating a new context, exhaust these alternatives:

1. **Pass props directly** — explicit data flow is easier to trace
2. **Compose children** — `<Layout><Posts posts={posts} /></Layout>` avoids prop drilling without context
3. **Extract a custom hook** — encapsulate logic without creating a provider

If you do create a context:

- Separate state and dispatch into two contexts (prevents unnecessary re-renders)
- Export custom consumer hooks (`useFoo()`) rather than exposing the raw context
- Place the provider at the narrowest possible scope

## localStorage

Used for client-side persistence that survives page reload:

- Page sizes (`page-size.ts`)
- Search/location history (`search.ts`, `location-history.ts`)
- Saved filter presets (`useSavedFilters`)
- Account status filter (`Accounts.tsx`)

These are **not** React state — they are external stores read/written through utility functions. Treat localStorage reads as side effects (in event handlers or Effects, not during render).
