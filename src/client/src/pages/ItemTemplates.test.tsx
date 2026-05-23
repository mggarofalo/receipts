import { screen } from "@testing-library/react";
import { renderWithProviders } from "@/test/test-utils";
import { mockQueryResult, mockMutationResult } from "@/test/mock-hooks";
import ItemTemplates from "./ItemTemplates";

vi.mock("@/hooks/usePageTitle", () => ({
  usePageTitle: vi.fn(),
}));

vi.mock("@/hooks/useEnumMetadata", () => ({
  useEnumMetadata: vi.fn(() => ({
    adjustmentTypes: [],
    authEventTypes: [],
    auditActions: [],
    entityTypes: [],
    adjustmentTypeLabels: {},
    authEventLabels: {},
    auditActionLabels: {},
    entityTypeLabels: {},
    isLoading: false,
  })),
}));

vi.mock("@/hooks/useItemTemplates", () => ({
  useItemTemplates: vi.fn(() => ({ data: [], total: 0, isLoading: false })),
  useCreateItemTemplate: vi.fn(() => ({ mutate: vi.fn(), isPending: false })),
  useUpdateItemTemplate: vi.fn(() => ({ mutate: vi.fn(), isPending: false })),
  useDeleteItemTemplates: vi.fn(() => ({ mutate: vi.fn(), isPending: false })),
  useHideItemTemplate: vi.fn(() => ({ mutate: vi.fn(), isPending: false })),
}));

vi.mock("@/hooks/usePermission", () => ({
  usePermission: vi.fn(() => ({
    roles: ["Admin"],
    hasRole: (role: string) => role === "Admin",
    isAdmin: () => true,
  })),
}));

vi.mock("@/hooks/useFuzzySearch", () => ({
  useFuzzySearch: vi.fn(() => ({
    search: "",
    setSearch: vi.fn(),
    results: [],
    totalCount: 0,
    isSearching: false,
    clearSearch: vi.fn(),
  })),
}));

vi.mock("@/hooks/useSavedFilters", () => ({
  useSavedFilters: vi.fn(() => ({
    filters: [],
    save: vi.fn(),
    remove: vi.fn(),
  })),
}));

vi.mock("@/hooks/useServerPagination", () => ({
  useServerPagination: vi.fn(() => ({
    offset: 0,
    limit: 25,
    currentPage: 1,
    pageSize: 25,
    totalPages: vi.fn(() => 1),
    setPage: vi.fn(),
    setPageSize: vi.fn(),
    resetPage: vi.fn(),
  })),
}));

vi.mock("@/hooks/useServerSort", () => ({
  useServerSort: vi.fn(() => ({
    sortBy: "name",
    sortDirection: "asc",
    toggleSort: vi.fn(),
  })),
}));

vi.mock("@/hooks/useListKeyboardNav", () => ({
  useListKeyboardNav: vi.fn(() => ({
    focusedId: null,
    setFocusedIndex: vi.fn(),
    tableRef: { current: null },
    containerProps: { role: "grid" as const, tabIndex: 0, "aria-label": "list", "aria-activedescendant": undefined },
    getRowProps: (id: string) => ({ id: `list-row-${id}`, role: "row" as const }),
  })),
}));

// Mocks needed by ItemTemplateForm (rendered inside dialogs)
vi.mock("@/hooks/useCategories", () => ({
  useAllCategories: vi.fn(() => ({ data: [], total: 0, isLoading: false })),
}));

vi.mock("@/hooks/useSubcategories", () => ({
  useAllSubcategoriesByCategoryId: vi.fn(() => ({ data: [], total: 0, isLoading: false })),
}));

vi.mock("@/hooks/usePagination", () => ({
  usePagination: vi.fn(() => ({
    paginatedItems: [],
    currentPage: 1,
    pageSize: 10,
    totalItems: 0,
    totalPages: 1,
    setPage: vi.fn(),
    setPageSize: vi.fn(),
  })),
}));

describe("ItemTemplates", () => {
  it("renders the page heading", () => {
    renderWithProviders(<ItemTemplates />);
    expect(
      screen.getByRole("heading", { name: /item templates/i }),
    ).toBeInTheDocument();
  });

  it("renders loading skeleton when data is loading", async () => {
    const { useItemTemplates } = await import("@/hooks/useItemTemplates");
    vi.mocked(useItemTemplates).mockReturnValue(mockQueryResult({
      data: undefined,
      isLoading: true,
    }));

    const { container } = renderWithProviders(<ItemTemplates />);
    expect(container.querySelector("[data-slot='skeleton']")).toBeInTheDocument();
  });

  it("renders empty state when no templates exist", async () => {
    const { useItemTemplates } = await import("@/hooks/useItemTemplates");
    vi.mocked(useItemTemplates).mockReturnValue(mockQueryResult({
      data: [],
      total: 0,
      isLoading: false,
    }));

    renderWithProviders(<ItemTemplates />);
    expect(
      screen.getByText(/no item templates yet/i),
    ).toBeInTheDocument();
  });

  it("renders the New Template button", () => {
    renderWithProviders(<ItemTemplates />);
    expect(
      screen.getByRole("button", { name: /new template/i }),
    ).toBeInTheDocument();
  });

  it("renders the search input", () => {
    renderWithProviders(<ItemTemplates />);
    expect(
      screen.getByPlaceholderText(/search templates/i),
    ).toBeInTheDocument();
  });

  it("renders table with item templates when data exists", async () => {
    const items = [
      { id: "1", name: "Coffee", description: "Morning coffee", defaultCategory: "Food", defaultSubcategory: "Drinks", defaultUnitPrice: 4.50, defaultUnitPriceCurrency: "USD", defaultItemCode: "COF-001" },
    ];

    const { useFuzzySearch } = await import("@/hooks/useFuzzySearch");
    vi.mocked(useFuzzySearch).mockReturnValue(mockQueryResult({
      search: "",
      setSearch: vi.fn(),
      results: items.map((item) => ({ item, matches: [], score: 0, refIndex: 0 })),
      totalCount: items.length,
      isSearching: false,
      clearSearch: vi.fn(),
    }));

    const { useItemTemplates } = await import("@/hooks/useItemTemplates");
    vi.mocked(useItemTemplates).mockReturnValue(mockQueryResult({
      data: items,
      total: items.length,
      isLoading: false,
    }));

    renderWithProviders(<ItemTemplates />);
    expect(screen.getByText("Coffee")).toBeInTheDocument();
    expect(screen.getByText("Food")).toBeInTheDocument();
    expect(screen.getByText("Drinks")).toBeInTheDocument();
  });

  it("closes edit dialog when dismissed", async () => {
    const user = (await import("@testing-library/user-event")).default.setup();
    const items = [
      { id: "1", name: "Coffee", description: "Morning coffee", defaultCategory: "Food", defaultSubcategory: "Drinks", defaultUnitPrice: 4.50, defaultUnitPriceCurrency: "USD", defaultItemCode: "COF-001" },
    ];

    const { useFuzzySearch } = await import("@/hooks/useFuzzySearch");
    vi.mocked(useFuzzySearch).mockReturnValue(mockQueryResult({
      search: "",
      setSearch: vi.fn(),
      results: items.map((item) => ({ item, matches: [], score: 0, refIndex: 0 })),
      totalCount: items.length,
      isSearching: false,
      clearSearch: vi.fn(),
    }));

    const { usePagination } = await import("@/hooks/usePagination");
    vi.mocked(usePagination).mockReturnValue({
      paginatedItems: items,
      currentPage: 1,
      pageSize: 10,
      totalItems: items.length,
      totalPages: 1,
      setPage: vi.fn(),
      setPageSize: vi.fn(),
    });

    renderWithProviders(<ItemTemplates />);
    await user.click(screen.getByRole("button", { name: /edit/i }));
    expect(screen.getByRole("heading", { name: /edit item template/i })).toBeInTheDocument();

    await user.keyboard("{Escape}");
    await vi.waitFor(() => {
      expect(screen.queryByRole("heading", { name: /edit item template/i })).not.toBeInTheDocument();
    });
  });

  it("closes create dialog when Cancel is clicked", async () => {
    const user = (await import("@testing-library/user-event")).default.setup();
    renderWithProviders(<ItemTemplates />);
    await user.click(screen.getByRole("button", { name: /new template/i }));
    expect(screen.getByRole("heading", { name: /create item template/i })).toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: /cancel/i }));
    await vi.waitFor(() => {
      expect(screen.queryByRole("heading", { name: /create item template/i })).not.toBeInTheDocument();
    });
  });

  it("opens create dialog when New Template button is clicked", async () => {
    const user = (await import("@testing-library/user-event")).default.setup();
    renderWithProviders(<ItemTemplates />);

    await user.click(screen.getByRole("button", { name: /new template/i }));

    expect(
      screen.getByRole("heading", { name: /create item template/i }),
    ).toBeInTheDocument();
  });

  it("opens edit dialog when Edit button is clicked", async () => {
    const user = (await import("@testing-library/user-event")).default.setup();
    const items = [
      { id: "1", name: "Coffee", description: "Morning coffee", defaultCategory: "Food", defaultSubcategory: "Drinks", defaultUnitPrice: 4.50, defaultUnitPriceCurrency: "USD", defaultItemCode: "COF-001" },
    ];

    const { useFuzzySearch } = await import("@/hooks/useFuzzySearch");
    vi.mocked(useFuzzySearch).mockReturnValue(mockQueryResult({
      search: "",
      setSearch: vi.fn(),
      results: items.map((item) => ({ item, matches: [], score: 0, refIndex: 0 })),
      totalCount: items.length,
      isSearching: false,
      clearSearch: vi.fn(),
    }));

    const { useItemTemplates } = await import("@/hooks/useItemTemplates");
    vi.mocked(useItemTemplates).mockReturnValue(mockQueryResult({
      data: items,
      total: items.length,
      isLoading: false,
    }));

    renderWithProviders(<ItemTemplates />);
    await user.click(screen.getByRole("button", { name: /edit/i }));

    expect(
      screen.getByRole("heading", { name: /edit item template/i }),
    ).toBeInTheDocument();
  });

  it("toggles checkbox selection and shows delete button", async () => {
    const user = (await import("@testing-library/user-event")).default.setup();
    const items = [
      { id: "1", name: "Coffee", description: "Morning coffee", defaultCategory: "Food", defaultSubcategory: "Drinks", defaultUnitPrice: 4.50, defaultUnitPriceCurrency: "USD", defaultItemCode: "COF-001" },
    ];

    const { useFuzzySearch } = await import("@/hooks/useFuzzySearch");
    vi.mocked(useFuzzySearch).mockReturnValue(mockQueryResult({
      search: "",
      setSearch: vi.fn(),
      results: items.map((item) => ({ item, matches: [], score: 0, refIndex: 0 })),
      totalCount: items.length,
      isSearching: false,
      clearSearch: vi.fn(),
    }));

    const { useItemTemplates } = await import("@/hooks/useItemTemplates");
    vi.mocked(useItemTemplates).mockReturnValue(mockQueryResult({
      data: items,
      total: items.length,
      isLoading: false,
    }));

    renderWithProviders(<ItemTemplates />);
    await user.click(screen.getByLabelText("Select Coffee"));

    expect(
      screen.getByRole("button", { name: /delete/i }),
    ).toBeInTheDocument();
  });

  it("submits edit form and calls updateItemTemplate.mutate", async () => {
    const user = (await import("@testing-library/user-event")).default.setup();
    const mockMutate = vi.fn();
    const { useUpdateItemTemplate } = await import("@/hooks/useItemTemplates");
    vi.mocked(useUpdateItemTemplate).mockReturnValue(mockMutationResult({
      mutate: mockMutate,
      isPending: false,
    }));

    const items = [
      { id: "1", name: "Coffee", description: "Morning coffee", defaultCategory: "Food", defaultSubcategory: "Drinks", defaultUnitPrice: 4.50, defaultUnitPriceCurrency: "USD", defaultItemCode: "COF-001" },
    ];

    const { useFuzzySearch } = await import("@/hooks/useFuzzySearch");
    vi.mocked(useFuzzySearch).mockReturnValue(mockQueryResult({
      search: "",
      setSearch: vi.fn(),
      results: items.map((item) => ({ item, matches: [], score: 0, refIndex: 0 })),
      totalCount: items.length,
      isSearching: false,
      clearSearch: vi.fn(),
    }));

    const { usePagination } = await import("@/hooks/usePagination");
    vi.mocked(usePagination).mockReturnValue({
      paginatedItems: items,
      currentPage: 1,
      pageSize: 10,
      totalItems: items.length,
      totalPages: 1,
      setPage: vi.fn(),
      setPageSize: vi.fn(),
    });

    renderWithProviders(<ItemTemplates />);
    await user.click(screen.getByRole("button", { name: /edit/i }));

    const nameInput = screen.getByLabelText(/^name/i);
    await user.clear(nameInput);
    await user.type(nameInput, "Updated Template");
    await user.click(screen.getByRole("button", { name: /update template/i }));

    await vi.waitFor(() => {
      expect(mockMutate).toHaveBeenCalled();
    });
  });

  it("submits create form and calls createItemTemplate.mutate", async () => {
    const user = (await import("@testing-library/user-event")).default.setup();
    const mockMutate = vi.fn();
    const { useCreateItemTemplate } = await import("@/hooks/useItemTemplates");
    vi.mocked(useCreateItemTemplate).mockReturnValue(mockMutationResult({
      mutate: mockMutate,
      isPending: false,
    }));

    renderWithProviders(<ItemTemplates />);
    await user.click(screen.getByRole("button", { name: /new template/i }));

    await user.type(screen.getByLabelText(/^name/i), "Coffee Template");
    await user.click(screen.getByRole("button", { name: /create template/i }));

    await vi.waitFor(() => {
      expect(mockMutate).toHaveBeenCalled();
    });
  });

  it("renders NoResults when search returns no matches", async () => {
    const { useItemTemplates } = await import("@/hooks/useItemTemplates");
    vi.mocked(useItemTemplates).mockReturnValue(mockQueryResult({
      data: [{ id: "1", name: "Coffee", description: "Morning coffee", defaultCategory: "Food", defaultSubcategory: "Drinks", defaultUnitPrice: 4.50, defaultUnitPriceCurrency: "USD", defaultItemCode: "COF-001" }],
      isLoading: false,
    }));

    const { useFuzzySearch } = await import("@/hooks/useFuzzySearch");
    vi.mocked(useFuzzySearch).mockReturnValue(mockQueryResult({
      search: "xyz",
      setSearch: vi.fn(),
      results: [],
      totalCount: 0,
      isSearching: false,
      clearSearch: vi.fn(),
    }));

    renderWithProviders(<ItemTemplates />);
    expect(screen.getByText(/try fewer keywords/i)).toBeInTheDocument();
  });

  it("opens delete dialog and confirms deletion", async () => {
    const user = (await import("@testing-library/user-event")).default.setup();
    const mockMutate = vi.fn();
    const { useDeleteItemTemplates } = await import("@/hooks/useItemTemplates");
    vi.mocked(useDeleteItemTemplates).mockReturnValue(mockMutationResult({
      mutate: mockMutate,
      isPending: false,
    }));

    const items = [
      { id: "1", name: "Coffee", description: "Morning coffee", defaultCategory: "Food", defaultSubcategory: "Drinks", defaultUnitPrice: 4.50, defaultUnitPriceCurrency: "USD", defaultItemCode: "COF-001" },
    ];

    const { useFuzzySearch } = await import("@/hooks/useFuzzySearch");
    vi.mocked(useFuzzySearch).mockReturnValue(mockQueryResult({
      search: "",
      setSearch: vi.fn(),
      results: items.map((item) => ({ item, matches: [], score: 0, refIndex: 0 })),
      totalCount: items.length,
      isSearching: false,
      clearSearch: vi.fn(),
    }));

    const { useItemTemplates } = await import("@/hooks/useItemTemplates");
    vi.mocked(useItemTemplates).mockReturnValue(mockQueryResult({
      data: items,
      total: items.length,
      isLoading: false,
    }));

    renderWithProviders(<ItemTemplates />);
    await user.click(screen.getByLabelText("Select Coffee"));
    await user.click(screen.getByRole("button", { name: /delete/i }));

    expect(
      screen.getByRole("heading", { name: /delete item templates/i }),
    ).toBeInTheDocument();

    const dialogDeleteBtn = screen
      .getAllByRole("button", { name: /delete/i })
      .find((btn) => btn.closest("[role='dialog']") !== null);
    if (dialogDeleteBtn) {
      await user.click(dialogDeleteBtn);
      expect(mockMutate).toHaveBeenCalledWith(["1"]);
    }
  });

  it("opens create dialog on shortcut:new-item event", async () => {
    const { act } = await import("@testing-library/react");
    renderWithProviders(<ItemTemplates />);

    act(() => {
      window.dispatchEvent(new Event("shortcut:new-item"));
    });

    await screen.findByRole("heading", { name: /create item template/i });
    expect(
      screen.getByRole("heading", { name: /create item template/i }),
    ).toBeInTheDocument();
  });

  it("opens create dialog when navigated with openNew state", async () => {
    renderWithProviders(<ItemTemplates />, {
      route: { pathname: "/item-templates", state: { openNew: true } },
    });

    await screen.findByRole("heading", { name: /create item template/i });
    expect(
      screen.getByRole("heading", { name: /create item template/i }),
    ).toBeInTheDocument();
  });

  it("shows Hide button in edit modal when user is admin", async () => {
    const user = (await import("@testing-library/user-event")).default.setup();
    const items = [
      { id: "1", name: "Coffee", description: "Morning coffee", defaultCategory: "Food", defaultSubcategory: "Drinks", defaultUnitPrice: 4.50, defaultUnitPriceCurrency: "USD", defaultItemCode: "COF-001" },
    ];

    const { useFuzzySearch } = await import("@/hooks/useFuzzySearch");
    vi.mocked(useFuzzySearch).mockReturnValue(mockQueryResult({
      search: "",
      setSearch: vi.fn(),
      results: items.map((item) => ({ item, matches: [], score: 0, refIndex: 0 })),
      totalCount: items.length,
      isSearching: false,
      clearSearch: vi.fn(),
    }));

    const { useItemTemplates } = await import("@/hooks/useItemTemplates");
    vi.mocked(useItemTemplates).mockReturnValue(mockQueryResult({
      data: items,
      total: items.length,
      isLoading: false,
    }));

    const { usePermission } = await import("@/hooks/usePermission");
    vi.mocked(usePermission).mockReturnValue({
      roles: ["Admin"],
      hasRole: (role: string) => role === "Admin",
      isAdmin: () => true,
    });

    renderWithProviders(<ItemTemplates />);
    await user.click(screen.getByRole("button", { name: /edit/i }));

    expect(screen.getByRole("heading", { name: /edit item template/i })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /^hide$/i })).toBeInTheDocument();
  });

  it("does not show Hide button in edit modal when user is not admin", async () => {
    const user = (await import("@testing-library/user-event")).default.setup();
    const items = [
      { id: "1", name: "Coffee", description: "Morning coffee", defaultCategory: "Food", defaultSubcategory: "Drinks", defaultUnitPrice: 4.50, defaultUnitPriceCurrency: "USD", defaultItemCode: "COF-001" },
    ];

    const { useFuzzySearch } = await import("@/hooks/useFuzzySearch");
    vi.mocked(useFuzzySearch).mockReturnValue(mockQueryResult({
      search: "",
      setSearch: vi.fn(),
      results: items.map((item) => ({ item, matches: [], score: 0, refIndex: 0 })),
      totalCount: items.length,
      isSearching: false,
      clearSearch: vi.fn(),
    }));

    const { useItemTemplates } = await import("@/hooks/useItemTemplates");
    vi.mocked(useItemTemplates).mockReturnValue(mockQueryResult({
      data: items,
      total: items.length,
      isLoading: false,
    }));

    const { usePermission } = await import("@/hooks/usePermission");
    vi.mocked(usePermission).mockReturnValue({
      roles: ["User"],
      hasRole: (role: string) => role === "User",
      isAdmin: () => false,
    });

    renderWithProviders(<ItemTemplates />);
    await user.click(screen.getByRole("button", { name: /edit/i }));

    expect(screen.getByRole("heading", { name: /edit item template/i })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /^hide$/i })).not.toBeInTheDocument();
  });

  it("calls hideItemTemplate.mutate when Hide button is clicked", async () => {
    const user = (await import("@testing-library/user-event")).default.setup();
    const mockMutate = vi.fn();

    const items = [
      { id: "1", name: "Coffee", description: "Morning coffee", defaultCategory: "Food", defaultSubcategory: "Drinks", defaultUnitPrice: 4.50, defaultUnitPriceCurrency: "USD", defaultItemCode: "COF-001" },
    ];

    const { useFuzzySearch } = await import("@/hooks/useFuzzySearch");
    vi.mocked(useFuzzySearch).mockReturnValue(mockQueryResult({
      search: "",
      setSearch: vi.fn(),
      results: items.map((item) => ({ item, matches: [], score: 0, refIndex: 0 })),
      totalCount: items.length,
      isSearching: false,
      clearSearch: vi.fn(),
    }));

    const { useItemTemplates, useHideItemTemplate } = await import("@/hooks/useItemTemplates");
    vi.mocked(useItemTemplates).mockReturnValue(mockQueryResult({
      data: items,
      total: items.length,
      isLoading: false,
    }));
    vi.mocked(useHideItemTemplate).mockReturnValue(mockMutationResult({
      mutate: mockMutate,
      isPending: false,
    }));

    const { usePermission } = await import("@/hooks/usePermission");
    vi.mocked(usePermission).mockReturnValue({
      roles: ["Admin"],
      hasRole: (role: string) => role === "Admin",
      isAdmin: () => true,
    });

    renderWithProviders(<ItemTemplates />);
    await user.click(screen.getByRole("button", { name: /edit/i }));
    await user.click(screen.getByRole("button", { name: /^hide$/i }));

    expect(mockMutate).toHaveBeenCalledWith("1", expect.objectContaining({
      onSuccess: expect.any(Function),
    }));
  });
});
