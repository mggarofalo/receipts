# Custom Hooks

Rules for writing and modifying hooks in `src/client/src/hooks/`.

## Hook return identity

Stabilize callbacks, arrays and objects only when a consumer depends on their identity.
Use `useCallback` for reactive callbacks used as dependencies and return the existing
reducer state when nothing changed. Do not wrap every hook return in `useMemo`.

Return unmodified TanStack Query results directly. Their tracked properties preserve
subscription granularity; consumers must destructure the fields they use and depend
on those fields rather than the whole query result. A custom projection is appropriate
when a hook adds a different public shape and its consumers need that boundary.

## Hook Categories

### Data hooks (`use[Entity].ts`)

Every API entity has a standard set of hooks:

| Hook | Purpose | Returns |
|------|---------|---------|
| `use[Entity]s(offset, limit, sortBy, sortDirection)` | Paginated list | `{ data, total, isLoading, ...query }` |
| `use[Entity](id)` | Single item | Standard `useQuery` return |
| `useCreate[Entity]()` | Create mutation | `useMutation` return |
| `useUpdate[Entity]()` | Update mutation | `useMutation` return |
| `useDelete[Entity]s()` | Bulk delete mutation | `useMutation` return with optimistic updates |

**Conventions:**

- Query keys: `["entity", "list", offset, limit, sortBy, sortDirection]` for lists, `["entity", id]` for singles
- List hooks unwrap the paginated response: return `{ data: query.data?.data, total: query.data?.total ?? 0 }`
- Mutations invalidate the entire entity query family on success: `queryClient.invalidateQueries({ queryKey: ["entity"] })`
- Show toasts in `onSuccess` and `onError` callbacks inside the mutation hook
- Use `enabled: !!id` for single-item queries that depend on an ID

### Client state hooks

| Hook | State type | Storage |
|------|-----------|---------|
| `useServerPagination` | `useReducer` | In-memory (page size in localStorage) |
| `useServerSort` | URL search params | URL |
| `useFuzzySearch` | `useState` + debounce | In-memory |
| `useSavedFilters` | `useState` | localStorage |
| `useDebouncedValue` | `useState` + timer | In-memory |

### Browser API hooks

| Hook | External system |
|------|----------------|
| `useKeyboardShortcut` | `window.addEventListener('keydown')` |
| `usePageTitle` | `document.title` |
| `useSignalR` | SignalR WebSocket connection |
| `useFormShortcuts` | `keydown` listener for Cmd/Ctrl+Enter |
| `useListKeyboardNav` | `keydown` listener for arrow keys |

## Writing a New Hook

### Naming

- Must start with `use`
- Data hooks: `use[Entity]` or `use[Entity]s`
- Mutation hooks: `useCreate[Entity]`, `useUpdate[Entity]`, `useDelete[Entity]s`
- UI hooks: `use[Behavior]` (e.g., `useFuzzySearch`, `useListKeyboardNav`)

### Structure template

```tsx
import { useCallback, useMemo } from "react";

interface UseThingOptions {
  enabled?: boolean;
}

interface UseThingReturn {
  value: string;
  update: (next: string) => void;
}

export function useThing({ enabled = true }: UseThingOptions = {}): UseThingReturn {
  const [value, setValue] = useState('');

  const update = useCallback((next: string) => {
    setValue(next);
  }, []);

  // This projection is useful when consumers require a stable dependency.
  return useMemo(() => ({ value, update }), [value, update]);
}
```

### When an Effect is appropriate in a hook

An Effect inside a custom hook is appropriate when the hook synchronizes with an external system. The same rules from [effects.md](effects.md) apply:

- Must have cleanup if it subscribes/connects
- Dependencies must match the code (never suppress the linter)
- One Effect per synchronization concern

```tsx
// GOOD — hook encapsulates an external system subscription
export function useOnlineStatus() {
  const [isOnline, setIsOnline] = useState(true);

  useEffect(() => {
    function handleOnline() { setIsOnline(true); }
    function handleOffline() { setIsOnline(false); }
    window.addEventListener('online', handleOnline);
    window.addEventListener('offline', handleOffline);
    return () => {
      window.removeEventListener('online', handleOnline);
      window.removeEventListener('offline', handleOffline);
    };
  }, []);

  return isOnline;
}
```

### Composing hooks

Prefer composing existing hooks over building from scratch. The list page pattern composes `useServerSort` + `useServerPagination` + `useFuzzySearch` + `useSavedFilters` — each hook is independently testable.

### Dependency array safety

When a hook accepts callback props, document whether they must be stable. If instability would cause an infinite loop, wrap with `useRef` to decouple:

```tsx
// If the caller might pass an unstable callback:
export function useThing(onChangeProp: (v: string) => void) {
  const onChangeRef = useRef(onChangeProp);
  onChangeRef.current = onChangeProp;

  useEffect(() => {
    // Use ref to avoid re-subscribing when callback identity changes
    someExternalSystem.subscribe(() => {
      onChangeRef.current(getValue());
    });
    return () => someExternalSystem.unsubscribe();
  }, []); // stable — ref doesn't trigger re-run
}
```

## Refs vs State in Hooks

| Use state when | Use refs when |
|---------------|---------------|
| Value is rendered in JSX | Value is only used by event handlers |
| Change should trigger re-render | Change should NOT trigger re-render |
| You need React's batching/scheduling | You need synchronous read/write |

Common ref use cases in this project:
- Timer IDs (`useSubmitTimeout`, `useDebouncedValue`)
- Previous-value tracking (prefer event handlers over this pattern)
- DOM element references (focus management)
