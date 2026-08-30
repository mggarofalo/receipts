# Component Patterns

Conventions for building pages, forms, and reusable components in this project.

## List Pages

Every entity list page (`Accounts.tsx`, `Receipts.tsx`, `Transactions.tsx`, etc.) follows a standard structure. When creating a new list page, replicate this pattern.

### Setup block

```tsx
function Entities() {
  usePageTitle("Entities");

  // 1. Server hooks
  const { sortBy, sortDirection, toggleSort } = useServerSort({ defaultSortBy: "name", defaultSortDirection: "asc" });
  const { offset, limit, currentPage, pageSize, totalPages, setPage, setPageSize, resetPage } = useServerPagination();
  const { data: entitiesData, total: serverTotal, isLoading } = useEntities(offset, limit, sortBy, sortDirection);

  // 2. Client state
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [createOpen, setCreateOpen] = useState(false);
  const [editEntity, setEditEntity] = useState<Entity | null>(null);
  const [deleteOpen, setDeleteOpen] = useState(false);
  const [filterValues, setFilterValues] = useState<FilterValues>({});

  const anyDialogOpen = createOpen || editEntity !== null || deleteOpen;

  // 3. Search and filters
  const { search, setSearch, results, totalCount, clearSearch } = useFuzzySearch({ data, config: SEARCH_CONFIG });
  const filteredResults = useMemo(() => applyFilters(...), [results, filterValues, search]);

  // 4. Keyboard navigation
  const { focusedId, setFocusedIndex, tableRef } = useListKeyboardNav({ items: filteredResults, ... });
}
```

### Sort + pagination reset

Handle pagination reset in the event handler, not in an Effect:

```tsx
const handleSort = (column: string) => {
  toggleSort(column);
  resetPage();
};

// Pass handleSort to SortableTableHead
<SortableTableHead columns={columns} sortBy={sortBy} sortDirection={sortDirection} onSort={handleSort} />
```

### Search config

Define search configuration as module-level constants:

```tsx
const SEARCH_CONFIG: FuseSearchConfig<Entity> = {
  keys: [
    { name: "primaryField", weight: 2 },
    { name: "secondaryField", weight: 1 },
  ],
};

const FILTER_FIELDS: FilterField[] = [
  { type: "dateRange", key: "date", label: "Date" },
  { type: "numberRange", key: "amount", label: "Amount" },
];
```

## Forms

All forms use React Hook Form + Zod. Forms are **presentation-only** — the parent component owns the mutation.

### Form component contract

```tsx
interface EntityFormProps {
  mode: "create" | "edit";
  defaultValues?: Partial<EntityFormValues>;
  onSubmit: (values: EntityFormValues) => void;
  onCancel: () => void;
  isSubmitting: boolean;
}
```

### Schema + types

```tsx
const schema = z.object({
  name: z.string().min(1, "Name is required"),
  isActive: z.boolean(),
});
type EntityFormValues = z.infer<typeof schema>;
```

### Parent handles mutations

```tsx
<Dialog open={createOpen} onOpenChange={setCreateOpen}>
  <EntityForm
    mode="create"
    onSubmit={(values) => {
      createEntity.mutate(values, {
        onSuccess: () => setCreateOpen(false),
      });
    }}
    onCancel={() => setCreateOpen(false)}
    isSubmitting={createEntity.isPending}
  />
</Dialog>
```

### Form field dependencies

When one field should reset another (e.g., category clears subcategory), handle it in the `onChange` handler — not in an Effect:

```tsx
function handleCategoryChange(value: string) {
  form.setValue("category", value);
  form.setValue("subcategory", "");
}
```

## Controlled vs Uncontrolled Components

| Pattern | When to use | Example |
|---------|-------------|---------|
| **Uncontrolled** (owns state) | Component is self-contained, parent doesn't need the value | Search input with local debounce |
| **Controlled** (parent owns state) | Parent coordinates multiple children, or state must be shared | Toggle where parent tracks `isOn` |

When in doubt, start uncontrolled and lift state up when coordination is needed.

## Component Identity and Keys

### Use `key` to reset state

When you need a component to fully reset when context changes, give it a key that changes:

```tsx
// Chat input resets when switching contacts
<ChatInput key={contact.id} contact={contact} />

// Wizard step resets when navigating back to it
<StepForm key={stepIndex} />
```

### Never define components inside other components

```tsx
// BAD — MyInput is recreated every render, losing all state
function Parent() {
  function MyInput() {
    const [text, setText] = useState('');
    return <input value={text} onChange={e => setText(e.target.value)} />;
  }
  return <MyInput />;
}

// GOOD — MyInput is stable
function MyInput() {
  const [text, setText] = useState('');
  return <input value={text} onChange={e => setText(e.target.value)} />;
}

function Parent() {
  return <MyInput />;
}
```

## Declarative UI Modeling

When building a new component with complex state, follow the 5-step process:

1. **Enumerate visual states** — list every distinct UI configuration (empty, loading, error, success, editing)
2. **Identify triggers** — what user actions or external events cause transitions
3. **Model with minimal state** — use a single `status` enum instead of multiple booleans
4. **Eliminate redundancy** — if it can be derived, don't store it
5. **Wire event handlers** — update state in handlers, not Effects

```tsx
// Step 3: One status variable, not three booleans
const [status, setStatus] = useState<'idle' | 'submitting' | 'success' | 'error'>('idle');
const isSubmitting = status === 'submitting';

// Step 4: Derive instead of store
const hasError = error !== null; // don't store isError separately
const isEmpty = items.length === 0; // don't store isEmpty separately
```

## UI Primitives

This project uses **shadcn/ui** (Radix UI + Tailwind CSS). Custom variants are defined with CVA in `*-variants.ts` files.

- Use the `cn()` utility for conditional class merging
- Prefer existing shadcn components over custom implementations
- Custom form controls: `Combobox`, `DateInput`, `CurrencyInput`, `PasswordInput`, `SubmitButton`

Shared picker controls must preserve the complete option set supplied by their
callers. Do not silently cap or slice options inside a reusable control: entity
pickers rely on server-side auto-pagination to make every value searchable and
selectable. If a list becomes too large to render efficiently, add explicit
virtualization or server-backed search with loading and error states instead of
discarding entries.
- Toasts: `sonner` — call `toast.success()` / `toast.error()` in mutation hooks
- Icons: `lucide-react`
