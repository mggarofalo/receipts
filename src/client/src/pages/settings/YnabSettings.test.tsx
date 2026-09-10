import type { Mock } from "vitest";
import { screen } from "@testing-library/react";
import { renderWithProviders } from "@/test/test-utils";
import { mockQueryResult, mockMutationResult } from "@/test/mock-hooks";
import YnabSettings from "./YnabSettings";

// Match a settled useStableQuery projection and its successful wire payload;
// loading/error fixtures remain genuinely unverified instead of claiming success.
function settingsQuery(
  fields: Record<string, unknown>,
  wireCategories = false,
) {
  const loading = fields.isLoading === true;
  const error = fields.isError === true;
  const collection = ["budgets", "accounts", "mappings", "categories"].find(
    (key) => key in fields,
  );
  const payload =
    fields.data ??
    (collection
      ? { [wireCategories ? "categories" : "data"]: fields[collection] }
      : "selectedBudgetId" in fields
        ? { selectedBudgetId: fields.selectedBudgetId }
        : "unmappedCategories" in fields
          ? { unmappedCategories: fields.unmappedCategories }
          : "rateLimitStatus" in fields
            ? fields.rateLimitStatus
            : "isConfigured" in fields
              ? {
                  isConfigured: fields.isConfigured,
                  isConnected: fields.isConnected,
                  lastSuccessfulSyncUtc: fields.lastSuccessfulSyncUtc,
                }
              : {
                  staleAccountMappingCount: fields.staleAccountMappingCount,
                  staleCategoryMappingCount: fields.staleCategoryMappingCount,
                });
  return mockQueryResult({
    data: loading || error ? undefined : payload,
    isLoading: loading,
    isPending: loading,
    isFetching: loading,
    isSuccess: !loading && !error,
    isError: error,
    isFetched: !loading,
    status: loading ? "pending" : error ? "error" : "success",
    fetchStatus: loading ? "fetching" : "idle",
    error: error ? new Error("Lookup unavailable") : null,
    ...fields,
  });
}

vi.mock("@/hooks/usePageTitle", () => ({
  usePageTitle: vi.fn(),
}));

vi.mock("@/hooks/useAccounts", () => ({
  useAllAccounts: vi.fn(() => settingsQuery({ data: [], isLoading: false })),
}));

vi.mock("@/hooks/useYnab", () => ({
  useYnabConnectionStatus: vi.fn(() =>
    settingsQuery({
      isConfigured: false,
      isConnected: false,
      lastSuccessfulSyncUtc: null,
      isLoading: false,
    }),
  ),
  useYnabBudgets: vi.fn(() =>
    settingsQuery({
      budgets: [{ id: "budget-1", name: "Budget" }],
      isLoading: false,
      isError: false,
    }),
  ),
  useSelectedYnabBudget: vi.fn(() =>
    settingsQuery({ selectedBudgetId: null, isLoading: false }),
  ),
  useSelectYnabBudget: vi.fn(() => mockMutationResult()),
  useYnabAccounts: vi.fn(() =>
    settingsQuery({ accounts: [], isLoading: false }),
  ),
  useYnabAccountMappings: vi.fn(() =>
    settingsQuery({ mappings: [], isLoading: false }),
  ),
  useCreateYnabAccountMapping: vi.fn(() => mockMutationResult()),
  useUpdateYnabAccountMapping: vi.fn(() => mockMutationResult()),
  useDeleteYnabAccountMapping: vi.fn(() => mockMutationResult()),
  useYnabCategories: vi.fn(() =>
    settingsQuery({ categories: [], isLoading: false }),
  ),
  useDistinctReceiptItemCategories: vi.fn(() =>
    settingsQuery({ categories: [], isLoading: false }),
  ),
  useYnabCategoryMappings: vi.fn(() =>
    settingsQuery({ mappings: [], isLoading: false }),
  ),
  useUnmappedCategories: vi.fn(() => settingsQuery({ unmappedCategories: [] })),
  useCreateYnabCategoryMapping: vi.fn(() => mockMutationResult()),
  useUpdateYnabCategoryMapping: vi.fn(() => mockMutationResult()),
  useDeleteYnabCategoryMapping: vi.fn(() => mockMutationResult()),
  useYnabRateLimitStatus: vi.fn(() => settingsQuery({ rateLimitStatus: null })),
  useStaleMappings: vi.fn(() =>
    settingsQuery({
      staleAccountMappingCount: 0,
      staleCategoryMappingCount: 0,
      hasStaleMappings: false,
    }),
  ),
  useClearStaleMappings: vi.fn(() => mockMutationResult()),
}));

vi.mock("@/components/YnabBulkSyncCard", () => ({
  YnabBulkSyncCard: () => (
    <div data-testid="ynab-bulk-sync-card">Bulk YNAB Sync</div>
  ),
}));

beforeEach(async () => {
  const hooks = await import("@/hooks/useYnab");
  const defaults = {
    useYnabConnectionStatus: {
      isConfigured: true,
      isConnected: true,
      lastSuccessfulSyncUtc: null,
    },
    useYnabBudgets: { budgets: [{ id: "budget-1", name: "Budget" }] },
    useSelectedYnabBudget: { selectedBudgetId: "budget-1" },
    useYnabAccounts: { accounts: [] },
    useYnabAccountMappings: { mappings: [] },
    useYnabCategories: { categories: [] },
    useDistinctReceiptItemCategories: { categories: [] },
    useYnabCategoryMappings: { mappings: [] },
    useUnmappedCategories: { unmappedCategories: [] },
    useYnabRateLimitStatus: { rateLimitStatus: null },
    useStaleMappings: {
      staleAccountMappingCount: 0,
      staleCategoryMappingCount: 0,
      hasStaleMappings: false,
    },
  };
  for (const [name, fields] of Object.entries(defaults)) {
    (hooks[name as keyof typeof hooks] as Mock).mockReturnValue(
      settingsQuery(fields, name === "useDistinctReceiptItemCategories"),
    );
  }
});

describe("YnabSettings – Connection Status", () => {
  it("shows 'Connected' badge when configured and connected", async () => {
    const { useYnabConnectionStatus } = await import("@/hooks/useYnab");
    vi.mocked(useYnabConnectionStatus).mockReturnValue(
      settingsQuery({
        isConfigured: true,
        isConnected: true,
        lastSuccessfulSyncUtc: null,
        isLoading: false,
      }),
    );

    renderWithProviders(<YnabSettings />);

    expect(screen.getByText("Connected")).toBeInTheDocument();
    expect(screen.getByText("No syncs yet")).toBeInTheDocument();
  });

  it("shows 'Connected' badge with last sync time when available", async () => {
    const { useYnabConnectionStatus } = await import("@/hooks/useYnab");
    const recentDate = new Date(Date.now() - 5 * 60000).toISOString();
    vi.mocked(useYnabConnectionStatus).mockReturnValue(
      settingsQuery({
        isConfigured: true,
        isConnected: true,
        lastSuccessfulSyncUtc: recentDate,
        isLoading: false,
      }),
    );

    renderWithProviders(<YnabSettings />);

    expect(screen.getByText("Connected")).toBeInTheDocument();
    expect(screen.getByText(/Last sync:/)).toBeInTheDocument();
  });

  it("shows 'Not Configured' badge when PAT is missing", async () => {
    const { useYnabConnectionStatus } = await import("@/hooks/useYnab");
    vi.mocked(useYnabConnectionStatus).mockReturnValue(
      settingsQuery({
        isConfigured: false,
        isConnected: false,
        lastSuccessfulSyncUtc: null,
        isLoading: false,
      }),
    );

    renderWithProviders(<YnabSettings />);

    expect(screen.getByText("Not Configured")).toBeInTheDocument();
  });

  it("shows 'Disconnected' badge when configured but connection fails", async () => {
    const { useYnabConnectionStatus } = await import("@/hooks/useYnab");
    vi.mocked(useYnabConnectionStatus).mockReturnValue(
      settingsQuery({
        isConfigured: true,
        isConnected: false,
        lastSuccessfulSyncUtc: null,
        isLoading: false,
      }),
    );

    renderWithProviders(<YnabSettings />);

    expect(screen.getByText("Disconnected")).toBeInTheDocument();
  });

  it("shows loading spinner while checking connection", async () => {
    const { useYnabConnectionStatus } = await import("@/hooks/useYnab");
    vi.mocked(useYnabConnectionStatus).mockReturnValue(
      settingsQuery({
        isConfigured: false,
        isConnected: false,
        lastSuccessfulSyncUtc: null,
        isLoading: true,
      }),
    );

    renderWithProviders(<YnabSettings />);

    expect(screen.getByText("Checking connection...")).toBeInTheDocument();
  });
});

describe("YnabSettings – Category Mapping", () => {
  it("hides the mapping cards when YNAB is not configured", async () => {
    const { useYnabConnectionStatus } = await import("@/hooks/useYnab");
    vi.mocked(useYnabConnectionStatus).mockReturnValue(
      settingsQuery({
        isConfigured: false,
        isConnected: false,
        lastSuccessfulSyncUtc: null,
      }),
    );
    const { useYnabBudgets } = await import("@/hooks/useYnab");
    vi.mocked(useYnabBudgets).mockReturnValue(
      settingsQuery({ budgets: [], isLoading: false, isError: true }),
    );

    renderWithProviders(<YnabSettings />);

    // Only the Connection Status card renders; the mapping cards (and their
    // empty "Configure YNAB to..." placeholders) are hidden until a PAT is set.
    expect(screen.getByText("Connection Status")).toBeInTheDocument();
    expect(screen.queryByText("Budget Selection")).not.toBeInTheDocument();
    expect(screen.queryByText("Account Mapping")).not.toBeInTheDocument();
    expect(screen.queryByText("Category Mapping")).not.toBeInTheDocument();
  });

  it("shows 'Select a budget above to map categories.' when selectedBudgetId is null and not in error state", async () => {
    const { useYnabBudgets, useSelectedYnabBudget } =
      await import("@/hooks/useYnab");
    vi.mocked(useYnabBudgets).mockReturnValue(
      settingsQuery({
        budgets: [{ id: "budget-1", name: "Budget" }],
        isLoading: false,
        isError: false,
      }),
    );
    vi.mocked(useSelectedYnabBudget).mockReturnValue(
      settingsQuery({ selectedBudgetId: null, isLoading: false }),
    );

    renderWithProviders(<YnabSettings />);

    expect(
      screen.getByText("Select a budget above to map categories."),
    ).toBeInTheDocument();
  });

  it("explains the mapping and export consequences of switching destination budgets", () => {
    renderWithProviders(<YnabSettings />);

    expect(
      screen.getByText(
        /mappings from previously selected budgets are preserved.*ignored/i,
      ),
    ).toBeInTheDocument();
    expect(
      screen.getByText(
        /switching.*budget.*map.*accounts.*categories.*re-export/i,
      ),
    ).toBeInTheDocument();
  });

  it("shows loading spinner when categoryMappingLoading is true", async () => {
    const { useYnabBudgets, useSelectedYnabBudget, useYnabCategories } =
      await import("@/hooks/useYnab");
    vi.mocked(useYnabBudgets).mockReturnValue(
      settingsQuery({
        budgets: [{ id: "budget-1", name: "Budget" }],
        isLoading: false,
        isError: false,
      }),
    );
    vi.mocked(useSelectedYnabBudget).mockReturnValue(
      settingsQuery({
        selectedBudgetId: "budget-1",
        isLoading: false,
      }),
    );
    vi.mocked(useYnabCategories).mockReturnValue(
      settingsQuery({ categories: [], isLoading: true }),
    );

    renderWithProviders(<YnabSettings />);

    expect(screen.getByText("Loading categories...")).toBeInTheDocument();
  });

  it("shows 'No receipt item categories found.' when preconditions met but categories is empty", async () => {
    const {
      useYnabBudgets,
      useSelectedYnabBudget,
      useYnabCategories,
      useDistinctReceiptItemCategories,
    } = await import("@/hooks/useYnab");
    vi.mocked(useYnabBudgets).mockReturnValue(
      settingsQuery({
        budgets: [{ id: "budget-1", name: "Budget" }],
        isLoading: false,
        isError: false,
      }),
    );
    vi.mocked(useSelectedYnabBudget).mockReturnValue(
      settingsQuery({
        selectedBudgetId: "budget-1",
        isLoading: false,
      }),
    );
    vi.mocked(useYnabCategories).mockReturnValue(
      settingsQuery({ categories: [], isLoading: false }),
    );
    vi.mocked(useDistinctReceiptItemCategories).mockReturnValue(
      settingsQuery({ categories: [], isLoading: false }, true),
    );

    renderWithProviders(<YnabSettings />);

    expect(
      screen.getByText(
        "No receipt item categories found. Create some receipts first.",
      ),
    ).toBeInTheDocument();
  });

  it("shows bulk sync card when YNAB is configured and budget is selected", async () => {
    const { useYnabBudgets, useSelectedYnabBudget } =
      await import("@/hooks/useYnab");
    vi.mocked(useYnabBudgets).mockReturnValue(
      settingsQuery({
        budgets: [{ id: "b1", name: "Budget" }],
        isLoading: false,
        isError: false,
      }),
    );
    vi.mocked(useSelectedYnabBudget).mockReturnValue(
      settingsQuery({ selectedBudgetId: "b1", isLoading: false }),
    );

    renderWithProviders(<YnabSettings />);

    expect(screen.getByTestId("ynab-bulk-sync-card")).toBeInTheDocument();
  });

  it("hides bulk sync card when YNAB is not configured", async () => {
    const { useYnabConnectionStatus } = await import("@/hooks/useYnab");
    vi.mocked(useYnabConnectionStatus).mockReturnValue(
      settingsQuery({
        isConfigured: false,
        isConnected: false,
        lastSuccessfulSyncUtc: null,
      }),
    );
    const { useYnabBudgets } = await import("@/hooks/useYnab");
    vi.mocked(useYnabBudgets).mockReturnValue(
      settingsQuery({ budgets: [], isLoading: false, isError: true }),
    );

    renderWithProviders(<YnabSettings />);

    expect(screen.queryByTestId("ynab-bulk-sync-card")).not.toBeInTheDocument();
  });

  it("hides bulk sync card when no budget is selected", async () => {
    const { useYnabBudgets, useSelectedYnabBudget } =
      await import("@/hooks/useYnab");
    vi.mocked(useYnabBudgets).mockReturnValue(
      settingsQuery({
        budgets: [{ id: "b1", name: "Budget" }],
        isLoading: false,
        isError: false,
      }),
    );
    vi.mocked(useSelectedYnabBudget).mockReturnValue(
      settingsQuery({ selectedBudgetId: null, isLoading: false }),
    );

    renderWithProviders(<YnabSettings />);

    expect(screen.queryByTestId("ynab-bulk-sync-card")).not.toBeInTheDocument();
  });

  it("renders mapping rows when fully configured with categories", async () => {
    const {
      useYnabBudgets,
      useSelectedYnabBudget,
      useYnabCategories,
      useDistinctReceiptItemCategories,
      useYnabCategoryMappings,
      useUnmappedCategories,
    } = await import("@/hooks/useYnab");
    vi.mocked(useYnabBudgets).mockReturnValue(
      settingsQuery({
        budgets: [{ id: "budget-1", name: "Budget" }],
        isLoading: false,
        isError: false,
      }),
    );
    vi.mocked(useSelectedYnabBudget).mockReturnValue(
      settingsQuery({
        selectedBudgetId: "budget-1",
        isLoading: false,
      }),
    );
    vi.mocked(useYnabCategories).mockReturnValue(
      settingsQuery({
        categories: [
          {
            id: "ynab-cat-1",
            name: "Groceries",
            categoryGroupName: "Everyday",
          },
        ],
        isLoading: false,
      }),
    );
    vi.mocked(useDistinctReceiptItemCategories).mockReturnValue(
      settingsQuery(
        {
          categories: ["Food", "Transport"],
          isLoading: false,
        },
        true,
      ),
    );
    vi.mocked(useYnabCategoryMappings).mockReturnValue(
      settingsQuery({ mappings: [], isLoading: false }),
    );
    vi.mocked(useUnmappedCategories).mockReturnValue(
      settingsQuery({ unmappedCategories: ["Food", "Transport"] }),
    );

    renderWithProviders(<YnabSettings />);

    expect(screen.getByText("Food")).toBeInTheDocument();
    expect(screen.getByText("Transport")).toBeInTheDocument();
  });
});

describe("YnabSettings – Rate Limit Card", () => {
  it("renders rate limit card when YNAB is configured and status is available", async () => {
    const { useYnabBudgets, useYnabRateLimitStatus } =
      await import("@/hooks/useYnab");
    vi.mocked(useYnabBudgets).mockReturnValue(
      settingsQuery({
        budgets: [{ id: "budget-1", name: "Budget" }],
        isLoading: false,
        isError: false,
      }),
    );
    vi.mocked(useYnabRateLimitStatus).mockReturnValue(
      settingsQuery({
        rateLimitStatus: {
          remainingRequests: 150,
          maxRequests: 200,
          requestsUsed: 50,
          windowResetAt: "2026-04-05T23:00:00Z",
          oldestRequestAt: "2026-04-05T22:00:00Z",
        },
      }),
    );

    renderWithProviders(<YnabSettings />);

    expect(screen.getByText("API Rate Limit")).toBeInTheDocument();
    expect(screen.getByText("50 / 200 requests used")).toBeInTheDocument();
    expect(screen.getByText("150 remaining")).toBeInTheDocument();
  });

  it("does not render rate limit card when YNAB is not configured", async () => {
    const { useYnabConnectionStatus } = await import("@/hooks/useYnab");
    vi.mocked(useYnabConnectionStatus).mockReturnValue(
      settingsQuery({
        isConfigured: false,
        isConnected: false,
        lastSuccessfulSyncUtc: null,
      }),
    );
    const { useYnabBudgets, useYnabRateLimitStatus } =
      await import("@/hooks/useYnab");
    vi.mocked(useYnabBudgets).mockReturnValue(
      settingsQuery({ budgets: [], isLoading: false, isError: true }),
    );
    vi.mocked(useYnabRateLimitStatus).mockReturnValue(
      settingsQuery({ rateLimitStatus: null }),
    );

    renderWithProviders(<YnabSettings />);

    expect(screen.queryByText("API Rate Limit")).not.toBeInTheDocument();
  });

  it("shows warning when quota is low", async () => {
    const { useYnabBudgets, useYnabRateLimitStatus } =
      await import("@/hooks/useYnab");
    vi.mocked(useYnabBudgets).mockReturnValue(
      settingsQuery({
        budgets: [{ id: "budget-1", name: "Budget" }],
        isLoading: false,
        isError: false,
      }),
    );
    vi.mocked(useYnabRateLimitStatus).mockReturnValue(
      settingsQuery({
        rateLimitStatus: {
          remainingRequests: 10,
          maxRequests: 200,
          requestsUsed: 190,
          windowResetAt: "2026-04-05T23:00:00Z",
          oldestRequestAt: "2026-04-05T22:00:00Z",
        },
      }),
    );

    renderWithProviders(<YnabSettings />);

    expect(screen.getByText(/API quota is running low/)).toBeInTheDocument();
  });

  it("rate limit bar has role=progressbar with correct aria attributes", async () => {
    const { useYnabBudgets, useYnabRateLimitStatus } =
      await import("@/hooks/useYnab");
    vi.mocked(useYnabBudgets).mockReturnValue(
      settingsQuery({
        budgets: [{ id: "budget-1", name: "Budget" }],
        isLoading: false,
        isError: false,
      }),
    );
    vi.mocked(useYnabRateLimitStatus).mockReturnValue(
      settingsQuery({
        rateLimitStatus: {
          remainingRequests: 150,
          maxRequests: 200,
          requestsUsed: 50,
          windowResetAt: "2026-04-05T23:00:00Z",
          oldestRequestAt: "2026-04-05T22:00:00Z",
        },
      }),
    );

    renderWithProviders(<YnabSettings />);

    const progressbar = screen.getByRole("progressbar");
    expect(progressbar).toBeInTheDocument();
    expect(progressbar).toHaveAttribute("aria-valuenow", "50");
    expect(progressbar).toHaveAttribute("aria-valuemin", "0");
    expect(progressbar).toHaveAttribute("aria-valuemax", "200");
    expect(progressbar).toHaveAttribute("aria-label", "API rate limit usage");
  });

  it("rate limit bar aria-valuenow reflects current usage", async () => {
    const { useYnabBudgets, useYnabRateLimitStatus } =
      await import("@/hooks/useYnab");
    vi.mocked(useYnabBudgets).mockReturnValue(
      settingsQuery({
        budgets: [{ id: "budget-1", name: "Budget" }],
        isLoading: false,
        isError: false,
      }),
    );
    vi.mocked(useYnabRateLimitStatus).mockReturnValue(
      settingsQuery({
        rateLimitStatus: {
          remainingRequests: 10,
          maxRequests: 200,
          requestsUsed: 190,
          windowResetAt: "2026-04-05T23:00:00Z",
          oldestRequestAt: "2026-04-05T22:00:00Z",
        },
      }),
    );

    renderWithProviders(<YnabSettings />);

    const progressbar = screen.getByRole("progressbar");
    expect(progressbar).toHaveAttribute("aria-valuenow", "190");
    expect(progressbar).toHaveAttribute("aria-valuemax", "200");
  });
});

describe("YnabSettings – Stale Mappings", () => {
  it("shows stale mapping banner when stale account mappings exist", async () => {
    const { useYnabBudgets, useSelectedYnabBudget, useStaleMappings } =
      await import("@/hooks/useYnab");
    vi.mocked(useYnabBudgets).mockReturnValue(
      settingsQuery({
        budgets: [{ id: "budget-1", name: "Budget" }],
        isLoading: false,
        isError: false,
      }),
    );
    vi.mocked(useSelectedYnabBudget).mockReturnValue(
      settingsQuery({ selectedBudgetId: "budget-1", isLoading: false }),
    );
    vi.mocked(useStaleMappings).mockReturnValue(
      settingsQuery({
        staleAccountMappingCount: 2,
        staleCategoryMappingCount: 0,
        hasStaleMappings: true,
      }),
    );

    renderWithProviders(<YnabSettings />);

    expect(screen.getByText(/2 account mapping\(s\)/)).toBeInTheDocument();
    expect(
      screen.getByText("Delete previous-budget mappings"),
    ).toBeInTheDocument();
  });

  it("shows stale mapping banner when stale category mappings exist", async () => {
    const { useYnabBudgets, useSelectedYnabBudget, useStaleMappings } =
      await import("@/hooks/useYnab");
    vi.mocked(useYnabBudgets).mockReturnValue(
      settingsQuery({
        budgets: [{ id: "budget-1", name: "Budget" }],
        isLoading: false,
        isError: false,
      }),
    );
    vi.mocked(useSelectedYnabBudget).mockReturnValue(
      settingsQuery({ selectedBudgetId: "budget-1", isLoading: false }),
    );
    vi.mocked(useStaleMappings).mockReturnValue(
      settingsQuery({
        staleAccountMappingCount: 0,
        staleCategoryMappingCount: 3,
        hasStaleMappings: true,
      }),
    );

    renderWithProviders(<YnabSettings />);

    expect(screen.getByText(/3 category mapping\(s\)/)).toBeInTheDocument();
    expect(
      screen.getByText("Delete previous-budget mappings"),
    ).toBeInTheDocument();
  });

  it("shows both account and category counts when both are stale", async () => {
    const { useYnabBudgets, useSelectedYnabBudget, useStaleMappings } =
      await import("@/hooks/useYnab");
    vi.mocked(useYnabBudgets).mockReturnValue(
      settingsQuery({
        budgets: [{ id: "budget-1", name: "Budget" }],
        isLoading: false,
        isError: false,
      }),
    );
    vi.mocked(useSelectedYnabBudget).mockReturnValue(
      settingsQuery({ selectedBudgetId: "budget-1", isLoading: false }),
    );
    vi.mocked(useStaleMappings).mockReturnValue(
      settingsQuery({
        staleAccountMappingCount: 2,
        staleCategoryMappingCount: 3,
        hasStaleMappings: true,
      }),
    );

    renderWithProviders(<YnabSettings />);

    expect(screen.getByText(/2 account mapping\(s\)/)).toBeInTheDocument();
    expect(screen.getByText(/3 category mapping\(s\)/)).toBeInTheDocument();
  });

  it("does not show stale mapping banner when no stale mappings exist", async () => {
    const { useYnabBudgets, useSelectedYnabBudget, useStaleMappings } =
      await import("@/hooks/useYnab");
    vi.mocked(useYnabBudgets).mockReturnValue(
      settingsQuery({
        budgets: [{ id: "budget-1", name: "Budget" }],
        isLoading: false,
        isError: false,
      }),
    );
    vi.mocked(useSelectedYnabBudget).mockReturnValue(
      settingsQuery({ selectedBudgetId: "budget-1", isLoading: false }),
    );
    vi.mocked(useStaleMappings).mockReturnValue(
      settingsQuery({
        staleAccountMappingCount: 0,
        staleCategoryMappingCount: 0,
        hasStaleMappings: false,
      }),
    );

    renderWithProviders(<YnabSettings />);

    expect(
      screen.queryByText("Delete previous-budget mappings"),
    ).not.toBeInTheDocument();
  });
});

it("renders unavailable budget feedback without claiming the configured integration is absent", async () => {
  const { useYnabBudgets } = await import("@/hooks/useYnab");
  vi.mocked(useYnabBudgets).mockReturnValue(
    settingsQuery({ budgets: [], isError: true }),
  );
  renderWithProviders(<YnabSettings />);
  expect(screen.getByText("Connected")).toBeInTheDocument();
  expect(
    screen.getByText(
      "YNAB budgets are unavailable. Any choices shown are last known.",
    ),
  ).toBeInTheDocument();
  expect(screen.getByRole("button", { name: "Retry" })).toBeInTheDocument();
  expect(screen.queryByText("Not Configured")).not.toBeInTheDocument();
  expect(screen.getByText("Budget Selection")).toBeInTheDocument();
  // Bulk sync retains its existing selected-budget contract; this test owns
  // budget lookup feedback, not that separate action workflow.
  expect(screen.getByTestId("ynab-bulk-sync-card")).toBeInTheDocument();
});
