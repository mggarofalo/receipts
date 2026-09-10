# Coding Standards

## C# Coding Standards

- Use explicit type declarations instead of `var` (exception: when type is obvious and enhances readability)
- Use primary constructors where appropriate
- Use `new(..)` target-typed syntax: `DatabaseMigratorService service = new(...);`
- Unit tests: xUnit with Arrange/Act/Assert structure
- Use `expected` and `actual` as variable names in test assertions
- Avoid testing implementation details
- Tag integration tests with `[Trait("Category", "Integration")]` — these are excluded from the unit-test step and pre-commit. Add `[Trait("Prerequisite", "Postgres")]` or `[Trait("Prerequisite", "Model")]` when the suite needs that external resource. Unit tests need no trait.
- **Moq `It.IsAny<T>()` rule:** Only use `It.IsAny<T>()` when the value genuinely doesn't matter for the test's assertion (e.g., `CancellationToken`). For domain-meaningful parameters like IDs, entity names, or any value the system under test is expected to pass through correctly, use specific values. This catches argument-ordering bugs where two parameters share the same type (e.g., `receiptId` and `accountId` are both `Guid`).

## Mapperly Rules

See also [docs/architecture.md](architecture.md#object-mapping-with-mapperly) for Mapperly code examples and patterns.

- Use Mapperly, not AutoMapper
- Use concrete mapper instances in tests — never mock mappers
- Don't use `[UseMapper(typeof(...))]` — instantiate mapper dependencies as fields and call explicitly
- Use `[MapperIgnoreTarget(nameof(XResponse.AdditionalProperties))]` on generated DTO mappings
- For aggregates with value objects: create manual mapping methods that delegate to Core mappers
- Generated DTOs live in `src/Presentation/API/Generated/` (namespace `API.Generated.Dtos`) — these files are gitignored and regenerated from `openapi/spec.yaml` on every `dotnet build`
- Naming: `CreateXRequest`, `UpdateXRequest`, `XResponse`

## EF Core Query Guidelines

Prefer **narrow projections** — always select the minimum required fields:
- Use `.Select()` to project only the columns/properties needed by the caller
- Use `.IgnoreAutoIncludes()` when navigation properties aren't needed for the query
- Avoid loading full entities when only a subset of fields is required
- For delete/update-only operations, prefer `ExecuteUpdateAsync`/`ExecuteDeleteAsync` over materializing entities
- Eliminate N+1 query patterns — use joined queries or batch operations instead of loops

## Frontend / TypeScript

### React Custom Hook Stability

All functions, objects, and arrays returned from custom hooks (`use*`) **must** be referentially stable:

- Wrap returned functions in `useCallback`
- Wrap returned objects/arrays in `useMemo`
- Ensure reducers return the same state reference when values haven't changed (bail out with `return state`)

**Why:** Consumers may place hook return values in `useEffect`/`useMemo`/`useCallback` dependency arrays. Unstable references cause infinite render loops that are invisible in static review and pass individual test files but hang the full test suite.

### TypeScript Conventions

- Use strict TypeScript (`strict: true` in `tsconfig.json`)
- Prefer `interface` over `type` for object shapes (better error messages, extensibility)
- Use generated API types from `src/client/src/generated/` — never hand-write request/response types
- Prefer named exports over default exports

### Component Conventions

- Use function components exclusively (no class components)
- Co-locate component tests with their source files or in `__tests__/` directories
- Use React Hook Form + Zod for all form handling — no uncontrolled forms
- Use TanStack Query for all server state — no `useEffect` + `useState` for API calls

## Validation and Code Quality

### LSP Server Checks

Always check the LSP for warnings when validating code changes:

- **Mapperly nullable mapping warnings**: Indicate design mismatches that can cause runtime NullReferenceExceptions — do not ignore
- **Unused usings, variables, or parameters**: Often indicate incomplete refactoring
- **Potential null reference warnings**: Important with nullable reference types enabled

**Workflow:** Build → Test → Check LSP diagnostics → Address warnings → Create follow-up issues for non-trivial fixes.
