# Client

React/Vite SPA (TypeScript) — the browser-based frontend for the Receipts application.

## Stack

| Technology | Purpose |
|------------|---------|
| React 19 | UI framework |
| Vite | Build tool and dev server |
| TypeScript | Type safety |
| TanStack Query | Server state management and caching |
| React Router | Client-side routing |
| React Hook Form + Zod | Form handling and validation |
| Tailwind CSS 4 | Utility-first styling |
| shadcn/ui + Radix UI | Accessible component primitives |
| Vitest | Unit testing |
| Playwright | E2E testing |

## Structure

- **`components/`** — Reusable UI components (forms, tables, dialogs, layout)
- **`pages/`** — Route-level page components
- **`hooks/`** — Custom React hooks (API queries, form state, UI state)
- **`contexts/`** — React context providers (auth, theme)
- **`generated/`** — TypeScript types and API client generated from `openapi/spec.yaml` (gitignored)
- **`lib/`** — Utility functions and shared configuration
- **`types/`** — Shared TypeScript type definitions
- **`test/`** — Test utilities and setup

## Custom Hook Conventions

All functions, objects, and arrays returned from custom hooks (`use*`) **must** be referentially stable:

- Wrap returned functions in `useCallback`
- Wrap returned objects/arrays in `useMemo`
- Ensure reducers return the same state reference when values haven't changed

Unstable references cause infinite render loops that pass individual tests but hang the full suite.

## Focus indicator ownership

The global `:focus-visible` rule in `src/index.css` owns every visible focus
indicator. Do not add focus ring, focus border, or outline suppression utilities
to components. This includes state-based variants such as `has-focus:ring-*`
and `group-data-[focused=true]:ring-*`.

The shadcn CLI can restore its default `focus-visible:ring-*` and
`outline-none` classes when it adds or updates a component. Remove those
classes before committing. A source-scanning test guards
`src/components/ui/` and explains any violation.

Transparent native controls may need a visible proxy indicator. Keep that
exception narrow and ensure the native control becomes visible in
forced-colors mode, as the calendar caption selects do.

## Development

```bash
npm install                    # Install dependencies
npm run dev                    # Start Vite dev server
npm run generate:types:write   # Regenerate types from OpenAPI spec (checked into git)
npm run generate:types:check   # Verify committed types match the current spec
npm run test                   # Run Vitest unit tests
npm run lint                   # Run ESLint
npx tsc -b --noEmit            # Type check
```

The dev server is automatically started by Aspire when using F5 debugging.
