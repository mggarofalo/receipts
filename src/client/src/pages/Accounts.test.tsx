import { screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { renderWithProviders } from "@/test/test-utils";
import { mockQueryResult } from "@/test/mock-hooks";
import { mockAccountResponse, mockCardResponse } from "@/test/mock-api";
import Accounts from "./Accounts";

vi.mock("@/hooks/usePageTitle", () => ({
  usePageTitle: vi.fn(),
}));

vi.mock("@/hooks/useAccounts", () => ({
  useAccounts: vi.fn(() => ({ data: [], total: 0, isLoading: false })),
  useAccountCards: vi.fn(() => ({ data: [], isLoading: false })),
  useCreateAccount: vi.fn(() => ({ mutate: vi.fn(), isPending: false })),
  useUpdateAccount: vi.fn(() => ({ mutate: vi.fn(), isPending: false })),
  useDeleteAccount: vi.fn(() => ({ mutate: vi.fn(), isPending: false })),
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

describe("Accounts", () => {
  beforeEach(() => {
    localStorage.removeItem("accounts-status-filter");
  });

  it("renders the page heading", () => {
    renderWithProviders(<Accounts />);
    expect(screen.getByRole("heading", { name: /accounts/i })).toBeInTheDocument();
  });

  it("renders loading skeleton when data is loading", async () => {
    const { useAccounts } = await import("@/hooks/useAccounts");
    vi.mocked(useAccounts).mockReturnValue(
      mockQueryResult({ data: undefined, isLoading: true }),
    );

    const { container } = renderWithProviders(<Accounts />);
    expect(container.querySelector("[data-slot='skeleton']")).toBeInTheDocument();
  });

  it("renders empty state when no accounts exist", async () => {
    const { useAccounts } = await import("@/hooks/useAccounts");
    vi.mocked(useAccounts).mockReturnValue(
      mockQueryResult({ data: [], total: 0, isLoading: false }),
    );

    renderWithProviders(<Accounts />);
    expect(screen.getByText(/no accounts yet/i)).toBeInTheDocument();
  });

  it("renders the New Account button", () => {
    renderWithProviders(<Accounts />);
    expect(screen.getByRole("button", { name: /new account/i })).toBeInTheDocument();
  });

  it("renders the search input", () => {
    renderWithProviders(<Accounts />);
    expect(screen.getByPlaceholderText(/search accounts/i)).toBeInTheDocument();
  });

  it("renders table with accounts when data exists", async () => {
    const items = [
      mockAccountResponse({ id: "1", name: "Apple Card" }),
      mockAccountResponse({ id: "2", name: "Chase Sapphire" }),
    ];
    const { useAccounts } = await import("@/hooks/useAccounts");
    vi.mocked(useAccounts).mockReturnValue(
      mockQueryResult({ data: items, total: items.length, isLoading: false }),
    );
    const { useFuzzySearch } = await import("@/hooks/useFuzzySearch");
    vi.mocked(useFuzzySearch).mockReturnValue({
      search: "",
      setSearch: vi.fn(),
      results: items.map((item) => ({ item, matches: [] as never })),
      totalCount: items.length,
      isSearching: false,
      clearSearch: vi.fn(),
      // satisfy any extra fields returned by hook
    } as unknown as ReturnType<typeof useFuzzySearch>);

    renderWithProviders(<Accounts />);

    expect(screen.getByText("Apple Card")).toBeInTheDocument();
    expect(screen.getByText("Chase Sapphire")).toBeInTheDocument();
  });

  it("expands an account row and lazy-loads its cards", async () => {
    const items = [mockAccountResponse({ id: "a1", name: "Apple Card" })];
    const { useAccounts, useAccountCards } = await import("@/hooks/useAccounts");
    vi.mocked(useAccounts).mockReturnValue(
      mockQueryResult({ data: items, total: 1, isLoading: false }),
    );

    const cards = [
      mockCardResponse({ id: "c1", cardCode: "VISA1", name: "Physical 1" }),
      mockCardResponse({ id: "c2", cardCode: "VISA2", name: "Physical 2" }),
    ];
    vi.mocked(useAccountCards).mockReturnValue(
      mockQueryResult({ data: cards, isLoading: false }),
    );

    const { useFuzzySearch } = await import("@/hooks/useFuzzySearch");
    vi.mocked(useFuzzySearch).mockReturnValue({
      search: "",
      setSearch: vi.fn(),
      results: items.map((item) => ({ item, matches: [] as never })),
      totalCount: items.length,
      isSearching: false,
      clearSearch: vi.fn(),
    } as unknown as ReturnType<typeof useFuzzySearch>);

    const user = userEvent.setup();
    renderWithProviders(<Accounts />);

    await user.click(screen.getByRole("button", { name: /expand cards/i }));

    expect(await screen.findByText("Physical 1")).toBeInTheDocument();
    expect(screen.getByText("Physical 2")).toBeInTheDocument();
    expect(screen.getByText("VISA1")).toBeInTheDocument();
  });

  it("shows 'No cards linked' when expanded account has no cards", async () => {
    const items = [mockAccountResponse({ id: "a1", name: "Apple Card" })];
    const { useAccounts, useAccountCards } = await import("@/hooks/useAccounts");
    vi.mocked(useAccounts).mockReturnValue(
      mockQueryResult({ data: items, total: 1, isLoading: false }),
    );
    vi.mocked(useAccountCards).mockReturnValue(
      mockQueryResult({ data: [], isLoading: false }),
    );

    const { useFuzzySearch } = await import("@/hooks/useFuzzySearch");
    vi.mocked(useFuzzySearch).mockReturnValue({
      search: "",
      setSearch: vi.fn(),
      results: items.map((item) => ({ item, matches: [] as never })),
      totalCount: items.length,
      isSearching: false,
      clearSearch: vi.fn(),
    } as unknown as ReturnType<typeof useFuzzySearch>);

    const user = userEvent.setup();
    renderWithProviders(<Accounts />);

    await user.click(screen.getByRole("button", { name: /expand cards/i }));

    expect(await screen.findByText(/no cards linked/i)).toBeInTheDocument();
  });

  it("opens create dialog on shortcut:new-item event", async () => {
    const { act } = await import("@testing-library/react");
    renderWithProviders(<Accounts />);

    act(() => {
      window.dispatchEvent(new Event("shortcut:new-item"));
    });

    await screen.findByRole("heading", { name: /create account/i });
    expect(
      screen.getByRole("heading", { name: /create account/i }),
    ).toBeInTheDocument();
  });

  it("opens create dialog when navigated with openNew state", async () => {
    renderWithProviders(<Accounts />, {
      route: { pathname: "/accounts", state: { openNew: true } },
    });

    await screen.findByRole("heading", { name: /create account/i });
    expect(
      screen.getByRole("heading", { name: /create account/i }),
    ).toBeInTheDocument();
  });
});

describe("status filter headings", () => {
  it("keeps filters usable without repeating their count and state under the heading", async () => {
    const user = (await import("@testing-library/user-event")).default.setup();
    const { useAccounts } = await import("@/hooks/useAccounts");
    vi.mocked(useAccounts).mockReturnValue(
      mockQueryResult({ data: [], total: 0, isLoading: false }),
    );
    renderWithProviders(<Accounts />);
    for (const label of ["Active", "Inactive", "All"]) {
      await user.click(screen.getByRole("tab", { name: label }));
      expect(screen.getByRole("tab", { name: label })).toHaveAttribute(
        "aria-selected",
        "true",
      );
      expect(
        screen.queryByText(/^\d+ total(?: · (?:active|inactive))?$/),
      ).not.toBeInTheDocument();
      expect(screen.getByRole("heading", { name: "Accounts" })).toBeVisible();
    }
  });
});
