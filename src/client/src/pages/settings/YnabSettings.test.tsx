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
}));

describe("YnabSettings – Category Mapping", () => {
  it("shows 'Configure YNAB to map categories.' when notConfigured is true", async () => {
    const { useYnabBudgets } = await import("@/hooks/useYnab");
    vi.mocked(useYnabBudgets).mockReturnValue(
      mockQueryResult({ budgets: [], isLoading: false, isError: true }),
    );

    renderWithProviders(<YnabSettings />);

    expect(
      screen.getByText("Configure YNAB to map categories."),
    ).toBeInTheDocument();
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
