import { screen } from "@testing-library/react";
import { renderWithProviders } from "@/test/test-utils";
import { mockQueryResult, mockMutationResult } from "@/test/mock-hooks";
import YnabSettings from "./YnabSettings";

vi.mock("@/hooks/usePageTitle", () => ({
  usePageTitle: vi.fn(),
}));

vi.mock("@/hooks/useAccounts", () => ({
  useAccounts: vi.fn(() =>
    mockQueryResult({ data: [], isLoading: false }),
  ),
}));

vi.mock("@/hooks/useYnab", () => ({
  useYnabConnectionStatus: vi.fn(() =>
    mockQueryResult({
      isConfigured: false,
      isConnected: false,
      lastSuccessfulSyncUtc: null,
      isLoading: false,
    }),
  ),
  useYnabBudgets: vi.fn(() =>
    mockQueryResult({ budgets: [], isLoading: false, isError: false }),
  ),
  useSelectedYnabBudget: vi.fn(() =>
    mockQueryResult({ selectedBudgetId: null, isLoading: false }),
  ),
  useSelectYnabBudget: vi.fn(() => mockMutationResult()),
  useYnabAccounts: vi.fn(() =>
    mockQueryResult({ accounts: [], isLoading: false }),
  ),
  useYnabAccountMappings: vi.fn(() =>
    mockQueryResult({ mappings: [], isLoading: false }),
  ),
  useCreateYnabAccountMapping: vi.fn(() => mockMutationResult()),
  useUpdateYnabAccountMapping: vi.fn(() => mockMutationResult()),
  useDeleteYnabAccountMapping: vi.fn(() => mockMutationResult()),
  useYnabCategories: vi.fn(() =>
    mockQueryResult({ categories: [], isLoading: false }),
  ),
  useDistinctReceiptItemCategories: vi.fn(() =>
    mockQueryResult({ categories: [], isLoading: false }),
  ),
  useYnabCategoryMappings: vi.fn(() =>
    mockQueryResult({ mappings: [], isLoading: false }),
  ),
  useUnmappedCategories: vi.fn(() =>
    mockQueryResult({ unmappedCategories: [] }),
  ),
  useCreateYnabCategoryMapping: vi.fn(() => mockMutationResult()),
  useUpdateYnabCategoryMapping: vi.fn(() => mockMutationResult()),
  useDeleteYnabCategoryMapping: vi.fn(() => mockMutationResult()),
  useYnabRateLimitStatus: vi.fn(() =>
    mockQueryResult({ rateLimitStatus: null }),
  ),
  useStaleMappings: vi.fn(() =>
    mockQueryResult({
      staleAccountMappingCount: 0,
      staleCategoryMappingCount: 0,
      hasStaleMappings: false,
    }),
  ),
  useClearStaleMappings: vi.fn(() => mockMutationResult()),
}));

vi.mock("@/components/YnabBulkSyncCard", () => ({
  YnabBulkSyncCard: () => <div data-testid="ynab-bulk-sync-card">Bulk YNAB Sync</div>,
}));

describe("YnabSettings – Connection Status", () => {
  it("shows 'Connected' badge when configured and connected", async () => {
    const { useYnabConnectionStatus } = await import("@/hooks/useYnab");
    vi.mocked(useYnabConnectionStatus).mockReturnValue(
      mockQueryResult({
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
      mockQueryResult({
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
      mockQueryResult({
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
      mockQueryResult({
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
      mockQueryResult({
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
    const { useYnabBudgets } = await import("@/hooks/useYnab");
    vi.mocked(useYnabBudgets).mockReturnValue(
      mockQueryResult({ budgets: [], isLoading: false, isError: true }),
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
    const { useYnabBudgets, useSelectedYnabBudget } = await import(
      "@/hooks/useYnab"
    );
    vi.mocked(useYnabBudgets).mockReturnValue(
      mockQueryResult({ budgets: [], isLoading: false, isError: false }),
    );
    vi.mocked(useSelectedYnabBudget).mockReturnValue(
      mockQueryResult({ selectedBudgetId: null, isLoading: false }),
    );

    renderWithProviders(<YnabSettings />);

    expect(
      screen.getByText("Select a budget above to map categories."),
    ).toBeInTheDocument();
  });

  it("shows loading spinner when categoryMappingLoading is true", async () => {
    const {
      useYnabBudgets,
      useSelectedYnabBudget,
      useYnabCategories,
    } = await import("@/hooks/useYnab");
    vi.mocked(useYnabBudgets).mockReturnValue(
      mockQueryResult({ budgets: [], isLoading: false, isError: false }),
    );
    vi.mocked(useSelectedYnabBudget).mockReturnValue(
      mockQueryResult({
        selectedBudgetId: "budget-1",
        isLoading: false,
      }),
    );
    vi.mocked(useYnabCategories).mockReturnValue(
      mockQueryResult({ categories: [], isLoading: true }),
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
      mockQueryResult({ budgets: [], isLoading: false, isError: false }),
    );
    vi.mocked(useSelectedYnabBudget).mockReturnValue(
      mockQueryResult({
        selectedBudgetId: "budget-1",
        isLoading: false,
      }),
    );
    vi.mocked(useYnabCategories).mockReturnValue(
      mockQueryResult({ categories: [], isLoading: false }),
    );
    vi.mocked(useDistinctReceiptItemCategories).mockReturnValue(
      mockQueryResult({ categories: [], isLoading: false }),
    );

    renderWithProviders(<YnabSettings />);

    expect(
      screen.getByText(
        "No receipt item categories found. Create some receipts first.",
      ),
    ).toBeInTheDocument();
  });

  it("shows bulk sync card when YNAB is configured and budget is selected", async () => {
    const { useYnabBudgets, useSelectedYnabBudget } = await import(
      "@/hooks/useYnab"
    );
    vi.mocked(useYnabBudgets).mockReturnValue(
      mockQueryResult({ budgets: [{ id: "b1", name: "Budget" }], isLoading: false, isError: false }),
    );
    vi.mocked(useSelectedYnabBudget).mockReturnValue(
      mockQueryResult({ selectedBudgetId: "b1", isLoading: false }),
    );

    renderWithProviders(<YnabSettings />);

    expect(screen.getByTestId("ynab-bulk-sync-card")).toBeInTheDocument();
  });

  it("hides bulk sync card when YNAB is not configured", async () => {
    const { useYnabBudgets } = await import("@/hooks/useYnab");
    vi.mocked(useYnabBudgets).mockReturnValue(
      mockQueryResult({ budgets: [], isLoading: false, isError: true }),
    );

    renderWithProviders(<YnabSettings />);

    expect(screen.queryByTestId("ynab-bulk-sync-card")).not.toBeInTheDocument();
  });

  it("hides bulk sync card when no budget is selected", async () => {
    const { useYnabBudgets, useSelectedYnabBudget } = await import(
      "@/hooks/useYnab"
    );
    vi.mocked(useYnabBudgets).mockReturnValue(
      mockQueryResult({ budgets: [{ id: "b1", name: "Budget" }], isLoading: false, isError: false }),
    );
    vi.mocked(useSelectedYnabBudget).mockReturnValue(
      mockQueryResult({ selectedBudgetId: null, isLoading: false }),
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
      mockQueryResult({ budgets: [], isLoading: false, isError: false }),
    );
    vi.mocked(useSelectedYnabBudget).mockReturnValue(
      mockQueryResult({
        selectedBudgetId: "budget-1",
        isLoading: false,
      }),
    );
    vi.mocked(useYnabCategories).mockReturnValue(
      mockQueryResult({
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
      mockQueryResult({
        categories: ["Food", "Transport"],
        isLoading: false,
      }),
    );
    vi.mocked(useYnabCategoryMappings).mockReturnValue(
      mockQueryResult({ mappings: [], isLoading: false }),
    );
    vi.mocked(useUnmappedCategories).mockReturnValue(
      mockQueryResult({ unmappedCategories: ["Food", "Transport"] }),
    );

    renderWithProviders(<YnabSettings />);

    expect(screen.getByText("Food")).toBeInTheDocument();
    expect(screen.getByText("Transport")).toBeInTheDocument();
  });
});

describe("YnabSettings – Rate Limit Card", () => {
  it("renders rate limit card when YNAB is configured and status is available", async () => {
    const { useYnabBudgets, useYnabRateLimitStatus } = await import(
      "@/hooks/useYnab"
    );
    vi.mocked(useYnabBudgets).mockReturnValue(
      mockQueryResult({ budgets: [], isLoading: false, isError: false }),
    );
    vi.mocked(useYnabRateLimitStatus).mockReturnValue(
      mockQueryResult({
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
    const { useYnabBudgets, useYnabRateLimitStatus } = await import(
      "@/hooks/useYnab"
    );
    vi.mocked(useYnabBudgets).mockReturnValue(
      mockQueryResult({ budgets: [], isLoading: false, isError: true }),
    );
    vi.mocked(useYnabRateLimitStatus).mockReturnValue(
      mockQueryResult({ rateLimitStatus: null }),
    );

    renderWithProviders(<YnabSettings />);

    expect(screen.queryByText("API Rate Limit")).not.toBeInTheDocument();
  });

  it("shows warning when quota is low", async () => {
    const { useYnabBudgets, useYnabRateLimitStatus } = await import(
      "@/hooks/useYnab"
    );
    vi.mocked(useYnabBudgets).mockReturnValue(
      mockQueryResult({ budgets: [], isLoading: false, isError: false }),
    );
    vi.mocked(useYnabRateLimitStatus).mockReturnValue(
      mockQueryResult({
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

    expect(
      screen.getByText(/API quota is running low/),
    ).toBeInTheDocument();
  });

  it("rate limit bar has role=progressbar with correct aria attributes", async () => {
    const { useYnabBudgets, useYnabRateLimitStatus } = await import(
      "@/hooks/useYnab"
    );
    vi.mocked(useYnabBudgets).mockReturnValue(
      mockQueryResult({ budgets: [], isLoading: false, isError: false }),
    );
    vi.mocked(useYnabRateLimitStatus).mockReturnValue(
      mockQueryResult({
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
    const { useYnabBudgets, useYnabRateLimitStatus } = await import(
      "@/hooks/useYnab"
    );
    vi.mocked(useYnabBudgets).mockReturnValue(
      mockQueryResult({ budgets: [], isLoading: false, isError: false }),
    );
    vi.mocked(useYnabRateLimitStatus).mockReturnValue(
      mockQueryResult({
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
      mockQueryResult({ budgets: [], isLoading: false, isError: false }),
    );
    vi.mocked(useSelectedYnabBudget).mockReturnValue(
      mockQueryResult({ selectedBudgetId: "budget-1", isLoading: false }),
    );
    vi.mocked(useStaleMappings).mockReturnValue(
      mockQueryResult({
        staleAccountMappingCount: 2,
        staleCategoryMappingCount: 0,
        hasStaleMappings: true,
      }),
    );

    renderWithProviders(<YnabSettings />);

    expect(screen.getByText(/2 account mapping\(s\)/)).toBeInTheDocument();
    expect(screen.getByText("Clear stale mappings")).toBeInTheDocument();
  });

  it("shows stale mapping banner when stale category mappings exist", async () => {
    const { useYnabBudgets, useSelectedYnabBudget, useStaleMappings } =
      await import("@/hooks/useYnab");
    vi.mocked(useYnabBudgets).mockReturnValue(
      mockQueryResult({ budgets: [], isLoading: false, isError: false }),
    );
    vi.mocked(useSelectedYnabBudget).mockReturnValue(
      mockQueryResult({ selectedBudgetId: "budget-1", isLoading: false }),
    );
    vi.mocked(useStaleMappings).mockReturnValue(
      mockQueryResult({
        staleAccountMappingCount: 0,
        staleCategoryMappingCount: 3,
        hasStaleMappings: true,
      }),
    );

    renderWithProviders(<YnabSettings />);

    expect(screen.getByText(/3 category mapping\(s\)/)).toBeInTheDocument();
    expect(screen.getByText("Clear stale mappings")).toBeInTheDocument();
  });

  it("shows both account and category counts when both are stale", async () => {
    const { useYnabBudgets, useSelectedYnabBudget, useStaleMappings } =
      await import("@/hooks/useYnab");
    vi.mocked(useYnabBudgets).mockReturnValue(
      mockQueryResult({ budgets: [], isLoading: false, isError: false }),
    );
    vi.mocked(useSelectedYnabBudget).mockReturnValue(
      mockQueryResult({ selectedBudgetId: "budget-1", isLoading: false }),
    );
    vi.mocked(useStaleMappings).mockReturnValue(
      mockQueryResult({
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
      mockQueryResult({ budgets: [], isLoading: false, isError: false }),
    );
    vi.mocked(useSelectedYnabBudget).mockReturnValue(
      mockQueryResult({ selectedBudgetId: "budget-1", isLoading: false }),
    );
    vi.mocked(useStaleMappings).mockReturnValue(
      mockQueryResult({
        staleAccountMappingCount: 0,
        staleCategoryMappingCount: 0,
        hasStaleMappings: false,
      }),
    );

    renderWithProviders(<YnabSettings />);

    expect(screen.queryByText("Clear stale mappings")).not.toBeInTheDocument();
  });
});
