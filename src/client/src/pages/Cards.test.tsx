import "@/test/setup-combobox-polyfills";
import { screen } from "@testing-library/react";
import { renderWithProviders } from "@/test/test-utils";
import { mockQueryResult, mockMutationResult } from "@/test/mock-hooks";
import { mockCardResponse } from "@/test/mock-api";
import Cards from "./Cards";

vi.mock("@/hooks/usePageTitle", () => ({
  usePageTitle: vi.fn(),
}));

vi.mock("@/hooks/useCards", () => ({
  useCards: vi.fn(() => ({ data: [], total: 0, isLoading: false })),
  useCreateCard: vi.fn(() => ({ mutate: vi.fn(), isPending: false })),
  useUpdateCard: vi.fn(() => ({ mutate: vi.fn(), isPending: false })),
  useDeleteCard: vi.fn(() => ({ mutate: vi.fn(), isPending: false })),
  useMergeCards: vi.fn(() => ({ mutateAsync: vi.fn(), isPending: false })),
  isMergeCardsConflict: vi.fn(() => false),
}));

const mockAccountList = [
  { id: "acct-1", name: "Primary Checking", isActive: true },
  { id: "acct-2", name: "Rewards Visa", isActive: true },
];

vi.mock("@/hooks/useAccounts", () => ({
  useAccounts: vi.fn(() => ({ data: mockAccountList, total: mockAccountList.length, isLoading: false })),
  useCreateAccount: vi.fn(() => ({ mutateAsync: vi.fn(), isPending: false })),
}));

vi.mock("@/hooks/usePermission", () => ({
  usePermission: vi.fn(() => ({
    roles: ["User"],
    hasRole: (role: string) => role === "User",
    isAdmin: () => false,
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

async function setAdmin(isAdmin: boolean) {
  const { usePermission } = await import("@/hooks/usePermission");
  vi.mocked(usePermission).mockReturnValue({
    roles: isAdmin ? ["Admin"] : ["User"],
    hasRole: (role: string) => role === (isAdmin ? "Admin" : "User"),
    isAdmin: () => isAdmin,
  });
}

describe("Cards", () => {
  beforeEach(async () => {
    localStorage.removeItem("cards-status-filter");
    // Nothing resets mocks between tests in this file, so pin the default
    // explicitly rather than letting an earlier test's admin state leak.
    await setAdmin(false);
  });

  it("renders the page heading", () => {
    renderWithProviders(<Cards />);
    expect(
      screen.getByRole("heading", { name: /cards/i }),
    ).toBeInTheDocument();
  });

  it("renders loading skeleton when data is loading", async () => {
    const { useCards } = await import("@/hooks/useCards");
    vi.mocked(useCards).mockReturnValue(mockQueryResult({
      data: undefined,
      isLoading: true,
    }));

    const { container } = renderWithProviders(<Cards />);
    expect(container.querySelector("[data-slot='skeleton']")).toBeInTheDocument();
  });

  it("renders empty state when no cards exist", async () => {
    const { useCards } = await import("@/hooks/useCards");
    vi.mocked(useCards).mockReturnValue(mockQueryResult({
      data: [],
      total: 0,
      isLoading: false,
    }));

    renderWithProviders(<Cards />);
    expect(
      screen.getByText(/no cards yet/i),
    ).toBeInTheDocument();
  });

  it("renders the New Card button", () => {
    renderWithProviders(<Cards />);
    expect(
      screen.getByRole("button", { name: /new card/i }),
    ).toBeInTheDocument();
  });

  it("renders the search input", () => {
    renderWithProviders(<Cards />);
    expect(
      screen.getByPlaceholderText(/search cards/i),
    ).toBeInTheDocument();
  });

  it("renders table with cards when data exists", async () => {
    const items = [
      mockCardResponse({ id: "1", cardCode: "CARD-001", name: "Checking" }),
      mockCardResponse({ id: "2", cardCode: "CARD-002", name: "Savings" }),
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

    const { useCards } = await import("@/hooks/useCards");
    vi.mocked(useCards).mockReturnValue(mockQueryResult({
      data: items,
      total: items.length,
      isLoading: false,
    }));

    renderWithProviders(<Cards />);
    expect(screen.getByText("Checking")).toBeInTheDocument();
    expect(screen.getByText("Savings")).toBeInTheDocument();
    expect(screen.getByText("CARD-001")).toBeInTheDocument();
  });

  it("opens create dialog when New Card button is clicked", async () => {
    const user = (await import("@testing-library/user-event")).default.setup();
    renderWithProviders(<Cards />);

    await user.click(screen.getByRole("button", { name: /new card/i }));

    expect(
      screen.getByRole("heading", { name: /create card/i }),
    ).toBeInTheDocument();
  });

  it("opens edit dialog when Edit button is clicked", async () => {
    const user = (await import("@testing-library/user-event")).default.setup();
    const items = [
      mockCardResponse({ id: "1", cardCode: "CARD-001", name: "Checking" }),
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

    const { useCards } = await import("@/hooks/useCards");
    vi.mocked(useCards).mockReturnValue(mockQueryResult({
      data: items,
      total: items.length,
      isLoading: false,
    }));

    renderWithProviders(<Cards />);
    await user.click(screen.getByRole("button", { name: /edit/i }));

    expect(
      screen.getByRole("heading", { name: /edit card/i }),
    ).toBeInTheDocument();
  });

  it("closes create dialog when Cancel is clicked", async () => {
    const user = (await import("@testing-library/user-event")).default.setup();
    renderWithProviders(<Cards />);
    await user.click(screen.getByRole("button", { name: /new card/i }));
    expect(screen.getByRole("heading", { name: /create card/i })).toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: /cancel/i }));
    await vi.waitFor(() => {
      expect(screen.queryByRole("heading", { name: /create card/i })).not.toBeInTheDocument();
    });
  });

  it("closes edit dialog when dismissed", async () => {
    const user = (await import("@testing-library/user-event")).default.setup();
    const items = [
      mockCardResponse({ id: "1", cardCode: "CARD-001", name: "Checking" }),
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

    renderWithProviders(<Cards />);
    await user.click(screen.getByRole("button", { name: /edit/i }));
    expect(screen.getByRole("heading", { name: /edit card/i })).toBeInTheDocument();

    await user.keyboard("{Escape}");
    await vi.waitFor(() => {
      expect(screen.queryByRole("heading", { name: /edit card/i })).not.toBeInTheDocument();
    });
  });

  it("opens create dialog on shortcut:new-item event", async () => {
    const { act } = await import("@testing-library/react");
    renderWithProviders(<Cards />);

    act(() => {
      window.dispatchEvent(new Event("shortcut:new-item"));
    });

    await screen.findByRole("heading", { name: /create card/i });
    expect(
      screen.getByRole("heading", { name: /create card/i }),
    ).toBeInTheDocument();
  });

  it("opens create dialog when navigated with openNew state", async () => {
    renderWithProviders(<Cards />, {
      route: { pathname: "/cards", state: { openNew: true } },
    });

    await screen.findByRole("heading", { name: /create card/i });
    expect(
      screen.getByRole("heading", { name: /create card/i }),
    ).toBeInTheDocument();
  });

  it("submits create form and calls createCard.mutate", async () => {
    const user = (await import("@testing-library/user-event")).default.setup();
    const mockMutate = vi.fn();
    const { useCreateCard } = await import("@/hooks/useCards");
    vi.mocked(useCreateCard).mockReturnValue(mockMutationResult({
      mutate: mockMutate,
      isPending: false,
    }));

    renderWithProviders(<Cards />);
    await user.click(screen.getByRole("button", { name: /new card/i }));

    await user.type(screen.getByLabelText(/card code/i), "CARD-NEW");
    await user.type(screen.getByLabelText(/^name/i), "New Card");
    await user.click(screen.getByRole("combobox", { name: /^account/i }));
    await user.click(await screen.findByText("Primary Checking"));
    await user.click(screen.getByRole("button", { name: /create card/i }));

    await vi.waitFor(() => {
      expect(mockMutate).toHaveBeenCalled();
    });
  });

  it("submits edit form and calls updateCard.mutate", async () => {
    const user = (await import("@testing-library/user-event")).default.setup();
    const mockMutate = vi.fn();
    const { useUpdateCard } = await import("@/hooks/useCards");
    vi.mocked(useUpdateCard).mockReturnValue(mockMutationResult({
      mutate: mockMutate,
      isPending: false,
    }));

    const items = [
      mockCardResponse({ id: "1", cardCode: "CARD-001", name: "Checking" }),
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

    renderWithProviders(<Cards />);
    await user.click(screen.getByRole("button", { name: /edit/i }));

    const nameInput = screen.getByLabelText(/^name/i);
    await user.clear(nameInput);
    await user.type(nameInput, "Updated Card");
    await user.click(screen.getByRole("button", { name: /update card/i }));

    await vi.waitFor(() => {
      expect(mockMutate).toHaveBeenCalled();
    });
  });

  it("renders NoResults when search returns no matches", async () => {
    const { useCards } = await import("@/hooks/useCards");
    vi.mocked(useCards).mockReturnValue(mockQueryResult({
      data: [mockCardResponse({ id: "1", cardCode: "CARD-001", name: "Checking" })],
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

    renderWithProviders(<Cards />);
    expect(screen.getByText(/try fewer keywords/i)).toBeInTheDocument();
  });

  it("defaults to showing only active cards (server-side filtered)", async () => {
    const activeItems = [
      mockCardResponse({ id: "1", cardCode: "CARD-001", name: "Checking", isActive: true }),
    ];

    const { useFuzzySearch } = await import("@/hooks/useFuzzySearch");
    vi.mocked(useFuzzySearch).mockReturnValue(mockQueryResult({
      search: "",
      setSearch: vi.fn(),
      results: activeItems.map((item) => ({ item, matches: [], score: 0, refIndex: 0 })),
      totalCount: activeItems.length,
      isSearching: false,
      clearSearch: vi.fn(),
    }));

    const { useCards } = await import("@/hooks/useCards");
    vi.mocked(useCards).mockReturnValue(mockQueryResult({
      data: activeItems,
      total: activeItems.length,
      isLoading: false,
    }));

    renderWithProviders(<Cards />);
    expect(screen.getByText("Checking")).toBeInTheDocument();

    const activeTab = screen.getByRole("tab", { name: "Active" });
    expect(activeTab).toHaveAttribute("data-state", "active");

    expect(useCards).toHaveBeenCalledWith(
      expect.anything(), expect.anything(), expect.anything(), expect.anything(), true,
    );
  });

  it("persists status filter in localStorage", async () => {
    const user = (await import("@testing-library/user-event")).default.setup();

    const { useCards } = await import("@/hooks/useCards");
    vi.mocked(useCards).mockReturnValue(mockQueryResult({
      data: [],
      total: 0,
      isLoading: false,
    }));

    renderWithProviders(<Cards />);
    await user.click(screen.getByRole("tab", { name: "All" }));

    expect(localStorage.getItem("cards-status-filter")).toBe("all");
  });

  it("renders a switch toggle on each card row", async () => {
    const items = [
      mockCardResponse({ id: "1", cardCode: "CARD-001", name: "Checking", isActive: true }),
      mockCardResponse({ id: "2", cardCode: "CARD-002", name: "Savings", isActive: false }),
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

    const { useCards } = await import("@/hooks/useCards");
    vi.mocked(useCards).mockReturnValue(mockQueryResult({
      data: items,
      total: items.length,
      isLoading: false,
    }));

    renderWithProviders(<Cards />);
    const switches = screen.getAllByRole("switch");
    expect(switches).toHaveLength(2);
    expect(switches[0]).toHaveAttribute("aria-checked", "true");
    expect(switches[1]).toHaveAttribute("aria-checked", "false");
  });

  it("calls updateCard.mutate when switch is toggled", async () => {
    const user = (await import("@testing-library/user-event")).default.setup();
    const mockMutate = vi.fn();
    const { useUpdateCard } = await import("@/hooks/useCards");
    vi.mocked(useUpdateCard).mockReturnValue(mockMutationResult({
      mutate: mockMutate,
      isPending: false,
    }));

    const items = [
      mockCardResponse({ id: "1", cardCode: "CARD-001", name: "Checking", isActive: true }),
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

    const { useCards } = await import("@/hooks/useCards");
    vi.mocked(useCards).mockReturnValue(mockQueryResult({
      data: items,
      total: items.length,
      isLoading: false,
    }));

    renderWithProviders(<Cards />);
    const switchEl = screen.getByRole("switch");
    await user.click(switchEl);

    expect(mockMutate).toHaveBeenCalledWith(
      expect.objectContaining({
        id: "1",
        cardCode: "CARD-001",
        name: "Checking",
        isActive: false,
      }),
    );
  });

  it("does not open edit dialog when switch is clicked", async () => {
    const user = (await import("@testing-library/user-event")).default.setup();
    const items = [
      mockCardResponse({ id: "1", cardCode: "CARD-001", name: "Checking", isActive: true }),
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

    const { useCards } = await import("@/hooks/useCards");
    vi.mocked(useCards).mockReturnValue(mockQueryResult({
      data: items,
      total: items.length,
      isLoading: false,
    }));

    renderWithProviders(<Cards />);
    const switchEl = screen.getByRole("switch");
    await user.click(switchEl);

    expect(screen.queryByRole("heading", { name: /edit card/i })).not.toBeInTheDocument();
  });

  it("passes isActive=false to useCards when Inactive tab is selected", async () => {
    const user = (await import("@testing-library/user-event")).default.setup();
    const inactiveItems = [
      mockCardResponse({ id: "2", cardCode: "CARD-002", name: "Savings", isActive: false }),
    ];

    const { useFuzzySearch } = await import("@/hooks/useFuzzySearch");
    vi.mocked(useFuzzySearch).mockReturnValue(mockQueryResult({
      search: "",
      setSearch: vi.fn(),
      results: inactiveItems.map((item) => ({ item, matches: [], score: 0, refIndex: 0 })),
      totalCount: inactiveItems.length,
      isSearching: false,
      clearSearch: vi.fn(),
    }));

    const { useCards } = await import("@/hooks/useCards");
    vi.mocked(useCards).mockReturnValue(mockQueryResult({
      data: inactiveItems,
      total: inactiveItems.length,
      isLoading: false,
    }));

    renderWithProviders(<Cards />);
    await user.click(screen.getByRole("tab", { name: "Inactive" }));

    expect(useCards).toHaveBeenCalledWith(
      expect.anything(), expect.anything(), expect.anything(), expect.anything(), false,
    );
  });

  it("renders the parent Account name in the table row", async () => {
    const items = [
      mockCardResponse({ id: "1", cardCode: "CARD-001", name: "Checking", accountId: "acct-1" }),
      mockCardResponse({ id: "2", cardCode: "CARD-002", name: "Savings" }),
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

    const { useCards } = await import("@/hooks/useCards");
    vi.mocked(useCards).mockReturnValue(mockQueryResult({
      data: items,
      total: items.length,
      isLoading: false,
    }));

    renderWithProviders(<Cards />);
    expect(screen.getByText("Primary Checking")).toBeInTheDocument();
  });

  it("preserves accountId when toggling active via the row switch", async () => {
    const user = (await import("@testing-library/user-event")).default.setup();
    const mockMutate = vi.fn();
    const { useUpdateCard } = await import("@/hooks/useCards");
    vi.mocked(useUpdateCard).mockReturnValue(mockMutationResult({
      mutate: mockMutate,
      isPending: false,
    }));

    const items = [
      mockCardResponse({ id: "1", cardCode: "CARD-001", name: "Checking", isActive: true, accountId: "acct-1" }),
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

    const { useCards } = await import("@/hooks/useCards");
    vi.mocked(useCards).mockReturnValue(mockQueryResult({
      data: items,
      total: items.length,
      isLoading: false,
    }));

    renderWithProviders(<Cards />);
    await user.click(screen.getByRole("switch"));

    expect(mockMutate).toHaveBeenCalledWith(
      expect.objectContaining({
        id: "1",
        isActive: false,
        accountId: "acct-1",
      }),
    );
  });

  it("forwards accountId to createCard.mutate when a parent Account is selected", async () => {
    const user = (await import("@testing-library/user-event")).default.setup();
    const mockMutate = vi.fn();
    const { useCreateCard } = await import("@/hooks/useCards");
    vi.mocked(useCreateCard).mockReturnValue(mockMutationResult({
      mutate: mockMutate,
      isPending: false,
    }));

    renderWithProviders(<Cards />);
    await user.click(screen.getByRole("button", { name: /new card/i }));

    await user.type(screen.getByLabelText(/card code/i), "CARD-NEW");
    await user.type(screen.getByLabelText(/^name/i), "New Card");
    await user.click(screen.getByRole("combobox", { name: /^account/i }));
    await user.click(await screen.findByText("Rewards Visa"));
    await user.click(screen.getByRole("button", { name: /create card/i }));

    await vi.waitFor(() => {
      expect(mockMutate).toHaveBeenCalledWith(
        expect.objectContaining({ accountId: "acct-2" }),
        expect.anything(),
      );
    });
  });

  it("merge button is enabled as soon as one card is selected", async () => {
    await setAdmin(true);
    const user = (await import("@testing-library/user-event")).default.setup();
    const items = [
      mockCardResponse({ id: "1", cardCode: "A1", name: "Alpha", isActive: true }),
      mockCardResponse({ id: "2", cardCode: "A2", name: "Beta", isActive: true }),
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
    const { useCards } = await import("@/hooks/useCards");
    vi.mocked(useCards).mockReturnValue(mockQueryResult({
      data: items,
      total: items.length,
      isLoading: false,
    }));

    renderWithProviders(<Cards />);

    const mergeButton = screen.getByRole("button", { name: /merge selected cards/i });
    // Nothing selected is still nothing to merge.
    expect(mergeButton).toBeDisabled();

    // RECEIPTS-887: one card used to be refused here, which is what made folding a
    // single-card account into another impossible without ticking unrelated cards.
    await user.click(screen.getByLabelText("Select Alpha"));
    expect(mergeButton).not.toBeDisabled();

    await user.click(screen.getByLabelText("Select Beta"));
    expect(mergeButton).not.toBeDisabled();
  });

  // RECEIPTS-895: POST /api/cards/merge is [Authorize(Policy = "RequireAdmin")].
  // Without a matching UI gate a non-admin could select cards, open the dialog,
  // create a target account and only then be rejected with a 403.
  describe("merge permission gate", () => {
    async function renderWithTwoCards() {
      const user = (await import("@testing-library/user-event")).default.setup();
      const items = [
        mockCardResponse({ id: "1", cardCode: "A1", name: "Alpha", isActive: true }),
        mockCardResponse({ id: "2", cardCode: "A2", name: "Beta", isActive: true }),
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
      const { useCards } = await import("@/hooks/useCards");
      vi.mocked(useCards).mockReturnValue(mockQueryResult({
        data: items,
        total: items.length,
        isLoading: false,
      }));

      renderWithProviders(<Cards />);
      return user;
    }

    it("keeps the merge button disabled for a non-admin even with 2 cards selected", async () => {
      const user = await renderWithTwoCards();

      await user.click(screen.getByLabelText("Select Alpha"));
      await user.click(screen.getByLabelText("Select Beta"));

      expect(
        screen.getByRole("button", { name: /merge selected cards/i }),
      ).toBeDisabled();
    });

    it("explains why merging is unavailable to a non-admin", async () => {
      await renderWithTwoCards();

      // Asserted on the accessible name rather than the tooltip: a disabled
      // control emits no pointer events, so the tooltip is sighted-hover only.
      // The name is what a keyboard or screen-reader user actually receives.
      expect(
        screen.getByRole("button", {
          name: /merge selected cards into an account — requires an administrator account/i,
        }),
      ).toBeInTheDocument();
    });

    it("does not clutter the admin's merge button with a reason", async () => {
      await setAdmin(true);
      await renderWithTwoCards();

      expect(
        screen.getByRole("button", { name: /merge selected cards/i }),
      ).toHaveAccessibleName("Merge selected cards into an account");
    });

    it("never opens the merge dialog for a non-admin", async () => {
      const user = await renderWithTwoCards();

      await user.click(screen.getByLabelText("Select Alpha"));
      await user.click(screen.getByLabelText("Select Beta"));
      await user.click(
        screen.getByRole("button", { name: /merge selected cards/i }),
      );

      expect(
        screen.queryByRole("dialog", { name: /merge/i }),
      ).not.toBeInTheDocument();
    });

    it("enables merging for an admin with 2 cards selected", async () => {
      await setAdmin(true);
      const user = await renderWithTwoCards();

      await user.click(screen.getByLabelText("Select Alpha"));
      await user.click(screen.getByLabelText("Select Beta"));

      expect(
        screen.getByRole("button", { name: /merge selected cards/i }),
      ).not.toBeDisabled();
    });
  });

});
