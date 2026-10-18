import { screen } from "@testing-library/react";
import { renderWithProviders } from "@/test/test-utils";
import { mockQueryResult, mockMutationResult } from "@/test/mock-hooks";
import Categories from "./Categories";

vi.mock("@/hooks/usePageTitle", () => ({
  usePageTitle: vi.fn(),
}));

vi.mock("@/hooks/useCategories", () => ({
  useCategories: vi.fn(() => ({ data: [], total: 0, isLoading: false })),
  useCreateCategory: vi.fn(() => ({ mutate: vi.fn(), isPending: false })),
  useUpdateCategory: vi.fn(() => ({ mutate: vi.fn(), isPending: false })),
  useDeleteCategory: vi.fn(() => ({ mutate: vi.fn(), isPending: false })),
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
    containerProps: {
      role: "grid" as const,
      tabIndex: 0,
      "aria-label": "list",
      "aria-activedescendant": undefined,
    },
    getRowProps: (id: string) => ({
      id: `list-row-${id}`,
      role: "row" as const,
    }),
  })),
}));

describe("Categories", () => {
  it("renders the page heading", () => {
    renderWithProviders(<Categories />);
    expect(
      screen.getByRole("heading", { name: /categories/i }),
    ).toBeInTheDocument();
  });

  it("renders loading skeleton when data is loading", async () => {
    const { useCategories } = await import("@/hooks/useCategories");
    vi.mocked(useCategories).mockReturnValue(
      mockQueryResult({
        data: undefined,
        isLoading: true,
      }),
    );

    const { container } = renderWithProviders(<Categories />);
    expect(
      container.querySelector("[data-slot='skeleton']"),
    ).toBeInTheDocument();
  });

  it("renders empty state when no categories exist", async () => {
    const { useCategories } = await import("@/hooks/useCategories");
    vi.mocked(useCategories).mockReturnValue(
      mockQueryResult({
        data: [],
        total: 0,
        isLoading: false,
      }),
    );

    renderWithProviders(<Categories />);
    expect(screen.getByText(/no categories yet/i)).toBeInTheDocument();
  });

  it("renders the New Category button", () => {
    renderWithProviders(<Categories />);
    expect(
      screen.getByRole("button", { name: /new category/i }),
    ).toBeInTheDocument();
  });

  it("renders the search input", () => {
    renderWithProviders(<Categories />);
    expect(
      screen.getByPlaceholderText(/search categories/i),
    ).toBeInTheDocument();
  });

  it("renders table with categories when data exists", async () => {
    const items = [
      { id: "1", name: "Food", description: "Food expenses", isActive: true },
      { id: "2", name: "Travel", description: null, isActive: true },
    ];

    const { useFuzzySearch } = await import("@/hooks/useFuzzySearch");
    vi.mocked(useFuzzySearch).mockReturnValue(
      mockQueryResult({
        search: "",
        setSearch: vi.fn(),
        results: items.map((item) => ({
          item,
          matches: [],
          score: 0,
          refIndex: 0,
        })),
        totalCount: items.length,
        isSearching: false,
        clearSearch: vi.fn(),
      }),
    );

    const { useCategories } = await import("@/hooks/useCategories");
    vi.mocked(useCategories).mockReturnValue(
      mockQueryResult({
        data: items,
        total: items.length,
        isLoading: false,
      }),
    );

    renderWithProviders(<Categories />);
    expect(screen.getByText("Food")).toBeInTheDocument();
    expect(screen.getByText("Travel")).toBeInTheDocument();
    expect(screen.getByText("Food expenses")).toBeInTheDocument();
  });

  it("opens create dialog when New Category button is clicked", async () => {
    const user = (await import("@testing-library/user-event")).default.setup();
    renderWithProviders(<Categories />);

    await user.click(screen.getByRole("button", { name: /new category/i }));

    expect(
      screen.getByRole("heading", { name: /create category/i }),
    ).toBeInTheDocument();
  });

  it("opens edit dialog when Edit button is clicked", async () => {
    const user = (await import("@testing-library/user-event")).default.setup();
    const items = [
      { id: "1", name: "Food", description: "Food expenses", isActive: true },
    ];

    const { useFuzzySearch } = await import("@/hooks/useFuzzySearch");
    vi.mocked(useFuzzySearch).mockReturnValue(
      mockQueryResult({
        search: "",
        setSearch: vi.fn(),
        results: items.map((item) => ({
          item,
          matches: [],
          score: 0,
          refIndex: 0,
        })),
        totalCount: items.length,
        isSearching: false,
        clearSearch: vi.fn(),
      }),
    );

    const { useCategories } = await import("@/hooks/useCategories");
    vi.mocked(useCategories).mockReturnValue(
      mockQueryResult({
        data: items,
        total: items.length,
        isLoading: false,
      }),
    );

    renderWithProviders(<Categories />);
    await user.click(screen.getByRole("button", { name: /edit/i }));

    expect(
      screen.getByRole("heading", { name: /edit category/i }),
    ).toBeInTheDocument();
  });

  it("submits create form and calls createCategory.mutate", async () => {
    const user = (await import("@testing-library/user-event")).default.setup();
    const mockMutate = vi.fn();
    const { useCreateCategory } = await import("@/hooks/useCategories");
    vi.mocked(useCreateCategory).mockReturnValue(
      mockMutationResult({
        mutate: mockMutate,
        isPending: false,
      }),
    );

    renderWithProviders(<Categories />);
    await user.click(screen.getByRole("button", { name: /new category/i }));

    await user.type(screen.getByLabelText(/^name/i), "New Category");
    await user.click(screen.getByRole("button", { name: /create category/i }));

    await vi.waitFor(() => {
      expect(mockMutate).toHaveBeenCalled();
    });
  });

  it("submits edit form and calls updateCategory.mutate", async () => {
    const user = (await import("@testing-library/user-event")).default.setup();
    const mockMutate = vi.fn();
    const { useUpdateCategory } = await import("@/hooks/useCategories");
    vi.mocked(useUpdateCategory).mockReturnValue(
      mockMutationResult({
        mutate: mockMutate,
        isPending: false,
      }),
    );

    const items = [
      { id: "1", name: "Food", description: "Food expenses", isActive: true },
    ];

    const { useFuzzySearch } = await import("@/hooks/useFuzzySearch");
    vi.mocked(useFuzzySearch).mockReturnValue(
      mockQueryResult({
        search: "",
        setSearch: vi.fn(),
        results: items.map((item) => ({
          item,
          matches: [],
          score: 0,
          refIndex: 0,
        })),
        totalCount: items.length,
        isSearching: false,
        clearSearch: vi.fn(),
      }),
    );
    const { useCategories } = await import("@/hooks/useCategories");
    vi.mocked(useCategories).mockReturnValue(
      mockQueryResult({ data: items, total: items.length, isLoading: false }),
    );

    renderWithProviders(<Categories />);
    await user.click(screen.getByRole("button", { name: /edit/i }));

    const nameInput = screen.getByLabelText(/^name/i);
    await user.clear(nameInput);
    await user.type(nameInput, "Updated Category");
    await user.click(screen.getByRole("button", { name: /update category/i }));

    await vi.waitFor(() => {
      expect(mockMutate).toHaveBeenCalled();
    });
  });

  it("renders NoResults when search returns no matches", async () => {
    const { useCategories } = await import("@/hooks/useCategories");
    vi.mocked(useCategories).mockReturnValue(
      mockQueryResult({
        data: [{ id: "1", name: "Food", description: "Food expenses" }],
        isLoading: false,
      }),
    );

    const { useFuzzySearch } = await import("@/hooks/useFuzzySearch");
    vi.mocked(useFuzzySearch).mockReturnValue(
      mockQueryResult({
        search: "xyz",
        setSearch: vi.fn(),
        results: [],
        totalCount: 0,
        isSearching: false,
        clearSearch: vi.fn(),
      }),
    );

    renderWithProviders(<Categories />);
    expect(screen.getByText(/try fewer keywords/i)).toBeInTheDocument();
  });

  it("closes create dialog when Cancel is clicked", async () => {
    const user = (await import("@testing-library/user-event")).default.setup();
    renderWithProviders(<Categories />);
    await user.click(screen.getByRole("button", { name: /new category/i }));
    expect(
      screen.getByRole("heading", { name: /create category/i }),
    ).toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: /cancel/i }));
    await vi.waitFor(() => {
      expect(
        screen.queryByRole("heading", { name: /create category/i }),
      ).not.toBeInTheDocument();
    });
  });

  it("closes edit dialog when dismissed", async () => {
    const user = (await import("@testing-library/user-event")).default.setup();
    const items = [
      { id: "1", name: "Food", description: "Food expenses", isActive: true },
    ];

    const { useFuzzySearch } = await import("@/hooks/useFuzzySearch");
    vi.mocked(useFuzzySearch).mockReturnValue(
      mockQueryResult({
        search: "",
        setSearch: vi.fn(),
        results: items.map((item) => ({
          item,
          matches: [],
          score: 0,
          refIndex: 0,
        })),
        totalCount: items.length,
        isSearching: false,
        clearSearch: vi.fn(),
      }),
    );
    const { useCategories } = await import("@/hooks/useCategories");
    vi.mocked(useCategories).mockReturnValue(
      mockQueryResult({ data: items, total: items.length, isLoading: false }),
    );

    renderWithProviders(<Categories />);
    await user.click(screen.getByRole("button", { name: /edit/i }));
    expect(
      screen.getByRole("heading", { name: /edit category/i }),
    ).toBeInTheDocument();

    await user.keyboard("{Escape}");
    await vi.waitFor(() => {
      expect(
        screen.queryByRole("heading", { name: /edit category/i }),
      ).not.toBeInTheDocument();
    });
  });

  it("opens create dialog on shortcut:new-item event", async () => {
    const { act } = await import("@testing-library/react");
    renderWithProviders(<Categories />);

    act(() => {
      window.dispatchEvent(new Event("shortcut:new-item"));
    });

    await screen.findByRole("heading", { name: /create category/i });
    expect(
      screen.getByRole("heading", { name: /create category/i }),
    ).toBeInTheDocument();
  });

  it("opens create dialog when navigated with openNew state", async () => {
    renderWithProviders(<Categories />, {
      route: { pathname: "/categories", state: { openNew: true } },
    });

    await screen.findByRole("heading", { name: /create category/i });
    expect(
      screen.getByRole("heading", { name: /create category/i }),
    ).toBeInTheDocument();
  });
});
