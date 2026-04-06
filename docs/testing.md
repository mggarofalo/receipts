# Testing

## .NET Backend

### Running Tests

```bash
# Unit tests only (same as CI and pre-commit)
dotnet test Receipts.slnx --filter "Category!=Integration"

# All tests including integration (requires ONNX model locally)
dotnet test Receipts.slnx

# Integration tests only
dotnet test Receipts.slnx --filter "Category=Integration"

# Single project
dotnet test tests/Application.Tests/Application.Tests.csproj

# Single test by name
dotnet test --filter "FullyQualifiedName~TestMethodName"
```

### Code Coverage

Coverage is collected via [coverlet](https://github.com/coverlet-coverage/coverlet) using the `XPlat Code Coverage` data collector and output in Cobertura XML format.

```bash
# Run tests with coverage
dotnet test Receipts.slnx --collect:"XPlat Code Coverage" --settings scripts/tests/coverlet.runsettings --results-directory TestResults

# Merge reports and generate HTML (requires dotnet-reportgenerator-globaltool)
reportgenerator -reports:TestResults/**/coverage.cobertura.xml -targetdir:coveragereport -reporttypes:Html
```

### Coverage Exclusions

Configured in `scripts/tests/coverlet.runsettings`:

| Exclusion | Reason |
|-----------|--------|
| `[*]*.Migrations.*` | EF Core migration classes are auto-generated |
| `**/Infrastructure/Migrations/*.cs` | Migration source files |
| `**/API/Generated/*.g.cs` | NSwag-generated DTOs from OpenAPI spec |
| `GeneratedCodeAttribute` | Any code marked with `[GeneratedCode]` (Mapperly, source generators) |
| `CompilerGeneratedAttribute` | Compiler-generated code (async state machines, lambdas, etc.) |
| Auto-properties | Skipped via `<SkipAutoProps>true</SkipAutoProps>` |
| Test assemblies | Excluded via `<IncludeTestAssembly>false</IncludeTestAssembly>` |

### Test Conventions

- **Framework:** xUnit
- **Mocking:** Moq
- **Assertions:** FluentAssertions
- **Naming:** `MethodName_Condition_ExpectedResult`
- **Structure:** Arrange / Act / Assert
- **Mappers:** Use concrete instances, never mock Mapperly mappers

### Integration Tests

Tests that require external resources (ONNX model files, real database) are tagged with `[Trait("Category", "Integration")]`. This separates them from fast unit tests.

```csharp
[Trait("Category", "Integration")]
public class OnnxEmbeddingServiceIntegrationTests : IClassFixture<OnnxEmbeddingServiceFixture>
{
    // Tests that run against the real ONNX model
}
```

**Key points:**
- CI and pre-commit hooks run with `--filter "Category!=Integration"` — integration tests are excluded
- Unit tests do NOT need a `[Trait]` attribute — they run everywhere by default
- Integration tests use `IClassFixture<T>` to share expensive resources (e.g., loading the ONNX model once for all tests in a class)
- The ONNX model (~90MB) is not in git; run `dotnet run scripts/download-onnx-model.cs` to download it locally
- Run `dotnet test --filter "Category=Integration"` to execute integration tests locally

## React Frontend

### Running Tests

```bash
# Run all tests (from src/client/)
npx vitest run

# Watch mode
npx vitest

# Run with coverage
npx vitest run --coverage
```

### Code Coverage

Coverage is collected via Vitest's built-in `@vitest/coverage-v8` provider, output in Cobertura XML, HTML, and text formats.

```bash
# Generate coverage report
npm run coverage

# Output: src/client/coverage/cobertura-coverage.xml (for CI)
# Output: src/client/coverage/ (HTML report, open index.html)
```

### Coverage Exclusions

Configured in `src/client/vite.config.ts` under `test.coverage.exclude`:

| Exclusion | Reason |
|-----------|--------|
| `src/generated/**` | OpenAPI-generated TypeScript types |
| `*.d.ts` | Type declaration files |
| `src/test/**` | Test setup and utilities |
| `src/main.tsx` | Application entry point (side-effect only) |

### Test Conventions

- **Framework:** Vitest (Jest-compatible API)
- **Component testing:** React Testing Library
- **User interactions:** `@testing-library/user-event`
- **DOM matchers:** `@testing-library/jest-dom`
- **Environment:** jsdom
- **No snapshot tests** (brittle, low coverage signal)
- Test files: `*.test.ts` / `*.test.tsx` colocated with source

### Mock Fidelity Rules

Mock data must match the real API response shape. When tests use simplified or incorrect mock shapes, they pass in isolation but hide integration bugs that surface at runtime.

**Core principle:** If the real API returns `{ data: T[], total, offset, limit }`, the mock must return that same envelope. Never mock a paginated endpoint as a bare array.

#### Paginated Response Shape

All list endpoints return a paginated envelope:

```typescript
// Real API response shape (from OpenAPI-generated types)
{
  data: T[],       // the array of items
  total: number,   // total count across all pages
  offset: number,  // current page offset
  limit: number,   // page size
}
```

Hooks like `useAccounts()` unwrap this: they return `{ ...query, data: query.data?.data, total: query.data?.total ?? 0 }`. But the **mock at the API client level** must still use the full envelope.

#### Correct vs Incorrect Mocks (Case Study)

A real bug occurred when hook-level tests mocked `client.GET` with a bare array instead of the paginated envelope. The hook's `query.data?.data` destructuring returned `undefined` at runtime because the mock shape didn't match reality.

```typescript
// WRONG: bare array -- hook's .data?.data unwrap returns undefined
(client.GET as Mock).mockResolvedValue({
  data: [{ id: "1", name: "Checking" }],
  error: undefined,
});

// CORRECT: paginated envelope matching real API shape
(client.GET as Mock).mockResolvedValue({
  data: { data: [{ id: "1", name: "Checking" }], total: 1, offset: 0, limit: 50 },
  error: undefined,
});
```

The same principle applies to single-entity endpoints (no envelope) and mutation responses. Match the real shape.

#### mockQueryResult and mockMutationResult

When testing **components** that consume hooks (not testing hooks themselves), use the helpers in `src/client/src/test/mock-hooks.ts`:

- `mockQueryResult(overrides)` -- creates a full `UseQueryResult` with all TanStack Query fields (`isSuccess`, `isPending`, `isFetching`, etc.). Pass overrides for the fields your test cares about.
- `mockMutationResult(overrides)` -- creates a full `UseMutationResult` with `mutate`, `mutateAsync`, `isPending`, etc.

**Why these exist:** Casting mocks with `as ReturnType<typeof useHook>` fails `tsc -b` because it misses required properties. These helpers provide type-safe defaults.

```typescript
// Component-level test: mock the hook return value
import { mockQueryResult, mockMutationResult } from "@/test/mock-hooks";

vi.mocked(useAccounts).mockReturnValue(mockQueryResult({
  data: [{ id: "1", accountCode: "ACC-001", name: "Checking", isActive: true }],
  total: 1,
  isLoading: false,
}));

vi.mocked(useCreateAccount).mockReturnValue(mockMutationResult({
  mutate: mockMutate,
  isPending: false,
}));
```

**When NOT to use them:** Hook-level tests (files like `useAccounts.test.ts`) that call `renderHook()` and test the hook directly. Those tests mock `client.GET`/`POST`/`PUT`/`DELETE` at the API client level and let the real hook execute.

#### mockApiSuccess and mockApiError

For hook-level tests that mock `client.GET`/`POST`/etc., optional helpers in `src/client/src/test/mock-api-client.ts` provide shorthand for success/error response shapes. To use them, you must wire the mock client into the module system with `vi.mock`:

```typescript
import mockClient, { mockApiSuccess, mockApiError, resetMockClient } from "@/test/mock-api-client";

// REQUIRED: wire mockClient as the module that hooks import
vi.mock("@/lib/api-client", () => ({ default: mockClient }));

beforeEach(() => resetMockClient());

// Then in tests:
mockClient.GET.mockResolvedValue(mockApiSuccess({ data: accounts, total: 1, offset: 0, limit: 50 }));
mockClient.GET.mockResolvedValue(mockApiError({ message: "Not found" }));
```

**Note:** Most existing hook tests use an inline `vi.mock` pattern instead (see the Hook-Level Tests section below). Either approach works — the key requirement is that `vi.mock("@/lib/api-client", ...)` is called so the mock is actually wired into the module system.

#### Mock Factories (mock-api.ts)

Reusable factory functions in `src/client/src/test/mock-api.ts` produce correctly-shaped API response objects matching the OpenAPI-generated DTOs. They make the correct mock shape the easy path and prevent shape mismatches that cause silent test failures.

**Item factories** create a single response object with sensible defaults. Pass a `Partial<T>` to override any field:

```typescript
import { mockAccountResponse, mockReceiptResponse, mockTransactionResponse } from "@/test/mock-api";

// Minimal: all required fields filled with defaults
const account = mockAccountResponse();

// Override specific fields for your test scenario
const checking = mockAccountResponse({ name: "Checking", accountCode: "ACC-001" });
const receipt = mockReceiptResponse({ location: "Walmart", taxAmount: 5.25 });
const txn = mockTransactionResponse({ amount: 42.00, date: "2025-03-01" });
```

Available item factories: `mockAccountResponse`, `mockCategoryResponse`, `mockSubcategoryResponse`, `mockReceiptResponse`, `mockReceiptItemResponse`, `mockTransactionResponse`, `mockAdjustmentResponse`, `mockItemTemplateResponse`.

**List factories** wrap items in the paginated envelope (`{ data, total, offset, limit }`):

```typescript
import { mockAccountListResponse, mockAccountResponse } from "@/test/mock-api";

// Default: one item with default values
const list = mockAccountListResponse();

// Custom items
const list2 = mockAccountListResponse([
  mockAccountResponse({ name: "Checking" }),
  mockAccountResponse({ name: "Savings", isActive: false }),
]);

// Override pagination metadata
const customItems = [mockAccountResponse({ name: "Business" })];
const page2 = mockAccountListResponse(customItems, { offset: 50, total: 200 });
```

**Generic paginated helper** for custom types:

```typescript
import { mockPaginatedResponse } from "@/test/mock-api";

const response = mockPaginatedResponse([{ custom: "data" }], { total: 100 });
// → { data: [{ custom: "data" }], total: 100, offset: 0, limit: 50 }
```

**ID generation**: Each factory call generates a unique deterministic ID. Call `resetMockIds()` in `beforeEach` if your tests depend on specific ID values.

**When to use**: In component-level tests alongside `mockQueryResult`/`mockMutationResult`, and in hook-level tests with `mockApiSuccess` to build realistic response payloads.

### Test Layers

Frontend tests fall into two layers with different mocking strategies:

#### Hook-Level Tests (`useX.test.ts`)

Test the hook in isolation. Mock at the **API client boundary**.

- Mock `@/lib/api-client` with `vi.mock()` so `client.GET`/`POST`/etc. are `vi.fn()`
- Mock `sonner` for toast assertions
- Use `renderHook()` with a `QueryClientProvider` wrapper
- Assert on the hook's return values (`data`, `isSuccess`, `isError`)
- Assert on the API client calls (`client.GET` called with correct path and params)

```typescript
vi.mock("@/lib/api-client", () => ({
  default: { GET: vi.fn(), POST: vi.fn(), PUT: vi.fn(), DELETE: vi.fn() },
}));

vi.mock("sonner", () => ({
  toast: { success: vi.fn(), error: vi.fn(), info: vi.fn() },
}));

// Then in the test:
(client.GET as Mock).mockResolvedValue({
  data: { data: accounts, total: 1, offset: 0, limit: 50 },
  error: undefined,
});

const { result } = renderHook(() => useAccounts(), { wrapper: createWrapper() });
await waitFor(() => expect(result.current.isSuccess).toBe(true));
```

#### Component-Level Tests (`Page.test.tsx`)

Test the rendered UI. Mock at the **hook boundary**.

- Mock the entire hook module with `vi.mock("@/hooks/useX", ...)`
- Use `mockQueryResult()`/`mockMutationResult()` for hook return values
- Use `renderWithProviders()` or `renderWithQueryClient()` from `@/test/test-utils`
- Assert on rendered DOM elements (`screen.getByRole`, `screen.getByText`)
- Assert on user interactions (`userEvent.click`, `userEvent.type`)

```typescript
vi.mock("@/hooks/useAccounts", () => ({
  useAccounts: vi.fn(() => ({ data: [], total: 0, isLoading: false })),
  useCreateAccount: vi.fn(() => ({ mutate: vi.fn(), isPending: false })),
  useUpdateAccount: vi.fn(() => ({ mutate: vi.fn(), isPending: false })),
}));

// Override for specific test:
vi.mocked(useAccounts).mockReturnValue(mockQueryResult({
  data: items,
  total: items.length,
  isLoading: false,
}));
```

#### Integration Tests (MSW)

Test hooks against a mock HTTP server for higher-fidelity validation.

- Use MSW (Mock Service Worker) with `setupServer()` from `msw/node`
- Setup file: `src/client/src/test/setup.integration.ts` starts and resets the server
- Test files: `*.integration.test.ts` (colocated with hook tests)
- Response bodies must match the real API response shape exactly

```typescript
import { http, HttpResponse } from "msw";
import { server } from "@/test/msw/server";

server.use(
  http.get("*/api/accounts", () => {
    return HttpResponse.json({
      data: [
        { id: "11111111-1111-1111-1111-111111111111", accountCode: "1000", name: "Cash", isActive: true },
      ],
      total: 1,
      offset: 0,
      limit: 50,
    });
  }),
);
```

### Test Utilities Reference

| File | Purpose |
|------|---------|
| `src/client/src/test/test-utils.tsx` | `renderWithProviders()`, `renderWithQueryClient()`, `createWrapper()`, `createQueryWrapper()`, `createQueryClient()` |
| `src/client/src/test/mock-hooks.ts` | `mockQueryResult()`, `mockMutationResult()` for component-level tests |
| `src/client/src/test/mock-api.ts` | `mockAccountResponse()`, `mockReceiptResponse()`, etc. -- type-safe factories for API response shapes |
| `src/client/src/test/mock-api-client.ts` | `mockApiSuccess()`, `mockApiError()`, `resetMockClient()` for hook-level tests |
| `src/client/src/test/setup.ts` | Global setup: imports `@testing-library/jest-dom`, polyfills `localStorage` |
| `src/client/src/test/setup.integration.ts` | Integration test setup: starts MSW server, resets handlers between tests |
| `src/client/src/test/setup-combobox-polyfills.ts` | Polyfills `ResizeObserver` and `scrollIntoView` for radix-ui/cmdk components |
| `src/client/src/test/msw/server.ts` | MSW server instance (`setupServer()`) |

## CI Coverage Reporting

Both stacks report coverage on every PR via GitHub Actions (`.github/workflows/github-ci.yml`):

- **Backend:** `irongut/CodeCoverageSummary` parses merged Cobertura XML, posts as a sticky PR comment (`coverage-backend` header)
- **Frontend:** Same action parses `coverage/cobertura-coverage.xml`, posts as a separate sticky PR comment (`coverage-frontend` header)

## Coverage Thresholds

Both stacks enforce minimum coverage as CI required status checks on `main`. PRs that drop coverage below these thresholds will fail CI.

| Stack | Line % | Branch % | Configured In |
|-------|--------|----------|---------------|
| Backend (.NET) | 78% | 70% | `irongut/CodeCoverageSummary` in `build` job |
| Frontend (React) | 80% | 80% | `irongut/CodeCoverageSummary` in `frontend-test-report` job |

Vitest also enforces local thresholds (75% statements, 65% branches, 70% functions, 78% lines) in `vite.config.ts`. These are disabled during CI shard runs (partial coverage would fail) but apply during local development.

Thresholds are set slightly below measured coverage to allow minor fluctuations. Raise them incrementally as test coverage improves.

## Agentic Testing Guide

For principles on how agents should approach test authoring — including test-first workflow, what makes a good test, and when coverage metrics don't apply — see **[docs/agentic-testing.md](agentic-testing.md)**.
