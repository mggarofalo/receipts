import "@/test/setup-combobox-polyfills";
import { screen, within } from "@testing-library/react";
import { renderWithProviders } from "@/test/test-utils";
import { mockQueryResult, mockMutationResult } from "@/test/mock-hooks";
import {
  mockReceiptListItemResponse,
  mockReceiptResponse,
} from "@/test/mock-api";
import Receipts from "./Receipts";

vi.mock("@/hooks/usePageTitle", () => ({
  usePageTitle: vi.fn(),
}));

vi.mock("@/hooks/useYnab", () => ({
  useReceiptYnabSyncStatuses: vi.fn(() => ({
    statusMap: new Map(),
    data: undefined,
    isLoading: false,
  })),
  useBulkPushYnabTransactions: vi.fn(() => ({
    mutate: vi.fn(),
    isPending: false,
  })),
}));

vi.mock("@/hooks/useReceipts", () => ({
  useReceipts: vi.fn(() => ({ data: [], total: 0, isLoading: false })),
  useCreateReceipt: vi.fn(() => ({ mutate: vi.fn(), isPending: false })),
  useUpdateReceipt: vi.fn(() => ({ mutate: vi.fn(), isPending: false })),
  useDeleteReceipts: vi.fn(() => ({ mutate: vi.fn(), isPending: false })),
  useLocationSuggestions: vi.fn(() => ({ data: undefined })),
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
    sortBy: "date",
    sortDirection: "desc",
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

describe("Receipts", () => {
  async function mockReceiptTable(
    items: ReturnType<typeof mockReceiptListItemResponse>[],
    statusMap: Map<string, string> = new Map(),
  ) {
    const { useFuzzySearch } = await import("@/hooks/useFuzzySearch");
    vi.mocked(useFuzzySearch).mockReturnValue(mockQueryResult({
      search: "",
      setSearch: vi.fn(),
      results: items.map((item) => ({ item, matches: [], score: 0, refIndex: 0 })),
      totalCount: items.length,
      isSearching: false,
      clearSearch: vi.fn(),
    }));

    const { useReceipts } = await import("@/hooks/useReceipts");
    vi.mocked(useReceipts).mockReturnValue(mockQueryResult({
      data: items,
      total: items.length,
      isLoading: false,
    }));

    const { useReceiptYnabSyncStatuses } = await import("@/hooks/useYnab");
    vi.mocked(useReceiptYnabSyncStatuses).mockReturnValue(mockQueryResult({ statusMap }));
  }

  it("renders the page heading", () => {
    renderWithProviders(<Receipts />);
    expect(
      screen.getByRole("heading", { level: 1, name: /^receipts$/i }),
    ).toBeInTheDocument();
  });

  it("renders loading skeleton when data is loading", async () => {
    const { useReceipts } = await import("@/hooks/useReceipts");
    vi.mocked(useReceipts).mockReturnValue(mockQueryResult({
      data: undefined,
      isLoading: true,
    }));

    const { container } = renderWithProviders(<Receipts />);
    expect(container.querySelector("[data-slot='skeleton']")).toBeInTheDocument();
  });

  it("renders empty state when no receipts exist", async () => {
    const { useReceipts } = await import("@/hooks/useReceipts");
    vi.mocked(useReceipts).mockReturnValue(mockQueryResult({
      data: [],
      total: 0,
      isLoading: false,
    }));

    renderWithProviders(<Receipts />);
    expect(
      screen.getByText(/no receipts yet/i),
    ).toBeInTheDocument();
  });

  it("renders the Quick Add button", () => {
    renderWithProviders(<Receipts />);
    expect(
      screen.getByRole("button", { name: /quick add/i }),
    ).toBeInTheDocument();
  });

  it("renders the search input", () => {
    renderWithProviders(<Receipts />);
    expect(
      screen.getByPlaceholderText(/search receipts/i),
    ).toBeInTheDocument();
  });

  it("renders table with receipts when data exists", async () => {
    const items = [
      mockReceiptResponse({ id: "1", location: "Walmart", date: "2024-01-15", taxAmount: 5.25 }),
      mockReceiptResponse({ id: "2", location: "Target", date: "2024-01-20", taxAmount: 3.50 }),
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

    const { useReceipts } = await import("@/hooks/useReceipts");
    vi.mocked(useReceipts).mockReturnValue(mockQueryResult({
      data: items,
      total: items.length,
      isLoading: false,
    }));

    renderWithProviders(<Receipts />);
    expect(screen.getByText("Walmart")).toBeInTheDocument();
    expect(screen.getByText("Target")).toBeInTheDocument();
  });

  it("renders the dense desktop fields without default Tax or short ID columns", async () => {
    const receipt = mockReceiptListItemResponse({
      id: "deadbeef-0000-4000-a000-000000000001",
      location: "Walmart",
      taxAmount: 123.45,
      expectedTotal: 42.5,
      paymentSummary: "Checking · Visa 4321",
      itemCount: 2,
      categorySummary: "Grocery, Household",
      balanceState: "balanced",
    });
    await mockReceiptTable([receipt]);

    renderWithProviders(<Receipts />);

    const table = screen.getByRole("grid", { name: "list" }).querySelector("table")!;
    const headers = within(table)
      .getAllByRole("columnheader")
      .map((header) => header.textContent?.trim() ?? "");
    expect(headers).toEqual([
      "",
      "Date",
      "Merchant",
      "Total",
      "Payment",
      "Contents",
      "Status",
      "Actions",
    ]);
    expect(screen.getByText("$42.50")).toHaveClass("receipt-total");
    expect(screen.getByText("Checking · Visa 4321")).toBeInTheDocument();
    expect(screen.getByText(/2 items/)).toBeInTheDocument();
    expect(screen.getByText(/Grocery, Household/)).toBeInTheDocument();
    expect(screen.queryByText("deadbeef")).not.toBeInTheDocument();
    expect(within(table).queryByRole("columnheader", { name: "Tax" })).not.toBeInTheDocument();
    expect(screen.queryByText("$123.45")).not.toBeInTheDocument();
  });

  it("sorts Total by expectedTotal and resets pagination", async () => {
    const toggleSort = vi.fn();
    const resetPage = vi.fn();
    const { useServerSort } = await import("@/hooks/useServerSort");
    vi.mocked(useServerSort).mockReturnValue({
      sortBy: "date",
      sortDirection: "desc",
      toggleSort,
    });
    const { useServerPagination } = await import("@/hooks/useServerPagination");
    vi.mocked(useServerPagination).mockReturnValue({
      offset: 0,
      limit: 25,
      currentPage: 1,
      pageSize: 25,
      totalPages: vi.fn(() => 1),
      setPage: vi.fn(),
      setPageSize: vi.fn(),
      resetPage,
    });
    await mockReceiptTable([mockReceiptListItemResponse()]);

    renderWithProviders(<Receipts />);
    await (await import("@testing-library/user-event")).default
      .setup()
      .click(screen.getByRole("button", { name: "Total" }));

    expect(toggleSort).toHaveBeenCalledWith("expectedTotal");
    expect(resetPage).toHaveBeenCalled();
  });

  it("marks responsive priority so Date, Merchant, Total, Status and actions remain", async () => {
    await mockReceiptTable([mockReceiptListItemResponse()]);
    renderWithProviders(<Receipts />);

    const table = document.querySelector<HTMLTableElement>("table.receipts-table")!;
    expect(table).toBeInTheDocument();
    expect(within(table).getByRole("columnheader", { name: "Payment" })).toHaveClass("receipt-col-secondary");
    expect(within(table).getByRole("columnheader", { name: "Contents" })).toHaveClass("receipt-col-secondary");

    const row = within(table).getAllByRole("row")[1];
    expect(row.querySelector(".receipt-date")).toBeInTheDocument();
    expect(row.querySelector(".receipt-merchant")).toBeInTheDocument();
    expect(row.querySelector(".receipt-total")).toBeInTheDocument();
    expect(row.querySelector(".receipt-status")).toBeInTheDocument();
    expect(row.querySelector(".receipt-actions")).toBeInTheDocument();
    expect(row.querySelectorAll(".receipt-col-secondary")).toHaveLength(2);
  });

  it("renders text and accessible labels for every balance and YNAB state", async () => {
    const items = [
      mockReceiptListItemResponse({ id: "balanced", balanceState: "balanced" }),
      mockReceiptListItemResponse({ id: "missing", balanceState: "noTransactions" }),
      mockReceiptListItemResponse({ id: "mismatch", balanceState: "outOfBalance" }),
    ];
    await mockReceiptTable(items, new Map([
      ["balanced", "Synced"],
      ["missing", "Pending"],
      ["mismatch", "Failed"],
    ]));

    renderWithProviders(<Receipts />);

    expect(screen.getByLabelText("Balance: balanced")).toHaveTextContent("Balanced");
    expect(screen.getByLabelText("Balance: no transactions")).toHaveTextContent("No transactions");
    expect(screen.getByLabelText("Balance: out of balance")).toHaveTextContent("Out of balance");
    expect(screen.getByLabelText("YNAB: synced")).toHaveTextContent("YNAB");
    expect(screen.getByLabelText("YNAB: pending")).toHaveTextContent("Pending");
    expect(screen.getByLabelText("YNAB: error")).toHaveTextContent("Error");
  });

  it("keeps compact actions isolated from row focus and links to the full receipt", async () => {
    const setFocusedIndex = vi.fn();
    const { useListKeyboardNav } = await import("@/hooks/useListKeyboardNav");
    vi.mocked(useListKeyboardNav).mockReturnValue({
      focusedIndex: -1,
      focusedId: null,
      setFocusedIndex,
      tableRef: { current: null },
      containerProps: { role: "grid", tabIndex: 0, "aria-label": "list", "aria-activedescendant": undefined },
      getRowProps: (id: string) => ({ id: `list-row-${id}`, role: "row" }),
    });
    const receipt = mockReceiptListItemResponse({ id: "receipt-123", location: "Target" });
    await mockReceiptTable([receipt]);

    renderWithProviders(<Receipts />);
    const viewLink = screen.getByRole("link", { name: "View" });
    expect(viewLink).toHaveAttribute("href", "/receipts/receipt-123");
    viewLink.focus();
    expect(viewLink).toHaveFocus();

    const user = (await import("@testing-library/user-event")).default.setup();
    await user.click(screen.getByRole("button", { name: "Edit" }));
    expect(setFocusedIndex).not.toHaveBeenCalled();
    expect(screen.getByRole("heading", { name: /edit receipt/i })).toBeInTheDocument();
  });

  it("opens create dialog when Quick Add button is clicked", async () => {
    const user = (await import("@testing-library/user-event")).default.setup();
    renderWithProviders(<Receipts />);

    await user.click(screen.getByRole("button", { name: /quick add/i }));

    expect(
      screen.getByRole("heading", { name: /create receipt/i }),
    ).toBeInTheDocument();
  });

  it("closes edit dialog when dismissed", async () => {
    const user = (await import("@testing-library/user-event")).default.setup();
    const items = [
      mockReceiptResponse({ id: "1", location: "Walmart", date: "2024-01-15", taxAmount: 5.25 }),
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

    renderWithProviders(<Receipts />);
    await user.click(screen.getByRole("button", { name: /edit/i }));
    expect(screen.getByRole("heading", { name: /edit receipt/i })).toBeInTheDocument();

    await user.keyboard("{Escape}");
    await vi.waitFor(() => {
      expect(screen.queryByRole("heading", { name: /edit receipt/i })).not.toBeInTheDocument();
    });
  });

  it("closes create dialog when Cancel is clicked", async () => {
    const user = (await import("@testing-library/user-event")).default.setup();
    renderWithProviders(<Receipts />);
    await user.click(screen.getByRole("button", { name: /quick add/i }));
    expect(screen.getByRole("heading", { name: /create receipt/i })).toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: /cancel/i }));
    await vi.waitFor(() => {
      expect(screen.queryByRole("heading", { name: /create receipt/i })).not.toBeInTheDocument();
    });
  });

  it("opens edit dialog when Edit button is clicked", async () => {
    const user = (await import("@testing-library/user-event")).default.setup();
    const items = [
      mockReceiptResponse({ id: "1", location: "Walmart", date: "2024-01-15", taxAmount: 5.25 }),
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

    const { useReceipts } = await import("@/hooks/useReceipts");
    vi.mocked(useReceipts).mockReturnValue(mockQueryResult({
      data: items,
      total: items.length,
      isLoading: false,
    }));

    renderWithProviders(<Receipts />);
    await user.click(screen.getByRole("button", { name: /edit/i }));

    expect(
      screen.getByRole("heading", { name: /edit receipt/i }),
    ).toBeInTheDocument();
  });

  it("toggles checkbox selection and shows delete button", async () => {
    const user = (await import("@testing-library/user-event")).default.setup();
    const items = [
      mockReceiptResponse({ id: "1", location: "Walmart", date: "2024-01-15", taxAmount: 5.25 }),
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

    const { useReceipts } = await import("@/hooks/useReceipts");
    vi.mocked(useReceipts).mockReturnValue(mockQueryResult({
      data: items,
      total: items.length,
      isLoading: false,
    }));

    renderWithProviders(<Receipts />);
    await user.click(screen.getByLabelText("Select Walmart"));

    expect(
      screen.getByRole("button", { name: /delete/i }),
    ).toBeInTheDocument();
  });

  it("submits edit form and calls updateReceipt.mutate", async () => {
    const user = (await import("@testing-library/user-event")).default.setup();
    const mockMutate = vi.fn();
    const { useUpdateReceipt } = await import("@/hooks/useReceipts");
    vi.mocked(useUpdateReceipt).mockReturnValue(mockMutationResult({
      mutate: mockMutate,
      isPending: false,
    }));

    const items = [
      mockReceiptResponse({ id: "1", location: "Walmart", date: "2024-01-15", taxAmount: 5.25 }),
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

    renderWithProviders(<Receipts />);
    await user.click(screen.getByRole("button", { name: /edit/i }));

    // Location is now a Combobox — open it, type a new value, and select it
    const combobox = screen.getByRole("combobox");
    await user.click(combobox);
    const searchInput = screen.getByPlaceholderText("Search locations...");
    await user.type(searchInput, "Target");
    await user.click(screen.getByText(/Use "Target"/));

    await user.click(screen.getByRole("button", { name: /update receipt/i }));

    await vi.waitFor(() => {
      expect(mockMutate).toHaveBeenCalled();
    });
  });

  it("submits create form and calls createReceipt.mutate", async () => {
    const user = (await import("@testing-library/user-event")).default.setup();
    const mockMutate = vi.fn();
    const { useCreateReceipt } = await import("@/hooks/useReceipts");
    vi.mocked(useCreateReceipt).mockReturnValue(mockMutationResult({
      mutate: mockMutate,
      isPending: false,
    }));

    renderWithProviders(<Receipts />);
    await user.click(screen.getByRole("button", { name: /quick add/i }));

    // Location is now a Combobox — open it, type a custom value, and select it
    const combobox = screen.getByRole("combobox");
    await user.click(combobox);
    const searchInput = screen.getByPlaceholderText("Search locations...");
    await user.type(searchInput, "Walmart");
    await user.click(screen.getByText(/Use "Walmart"/));

    const dateInput = screen.getByPlaceholderText("MM/DD/YYYY");
    await user.type(dateInput, "01/15/2024");
    await user.tab(); // Commit on blur
    await user.type(screen.getByLabelText(/tax amount/i), "5.25");
    await user.click(screen.getByRole("button", { name: /create receipt/i }));

    await vi.waitFor(() => {
      expect(mockMutate).toHaveBeenCalled();
    });
  });

  it("renders NoResults when search returns no matches", async () => {
    const { useReceipts } = await import("@/hooks/useReceipts");
    vi.mocked(useReceipts).mockReturnValue(mockQueryResult({
      data: [mockReceiptResponse({ id: "1", location: "Walmart", date: "2024-01-15", taxAmount: 5.25 })],
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

    renderWithProviders(<Receipts />);
    expect(screen.getByText(/try fewer keywords/i)).toBeInTheDocument();
  });

  it("opens delete dialog and confirms deletion", async () => {
    const user = (await import("@testing-library/user-event")).default.setup();
    const mockMutate = vi.fn();
    const { useDeleteReceipts } = await import("@/hooks/useReceipts");
    vi.mocked(useDeleteReceipts).mockReturnValue(mockMutationResult({
      mutate: mockMutate,
      isPending: false,
    }));

    const items = [
      mockReceiptResponse({ id: "1", location: "Walmart", date: "2024-01-15", taxAmount: 5.25 }),
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

    const { useReceipts } = await import("@/hooks/useReceipts");
    vi.mocked(useReceipts).mockReturnValue(mockQueryResult({
      data: items,
      total: items.length,
      isLoading: false,
    }));

    renderWithProviders(<Receipts />);
    await user.click(screen.getByLabelText("Select Walmart"));
    await user.click(screen.getByRole("button", { name: /delete/i }));

    expect(
      screen.getByRole("heading", { name: /delete receipts/i }),
    ).toBeInTheDocument();

    const dialogDeleteBtn = screen
      .getAllByRole("button", { name: /delete/i })
      .find((btn) => btn.closest("[role='dialog']") !== null);
    if (dialogDeleteBtn) {
      await user.click(dialogDeleteBtn);
      expect(mockMutate).toHaveBeenCalledWith(["1"]);
    }
  });

  it("toggles select all checkbox", async () => {
    const user = (await import("@testing-library/user-event")).default.setup();
    const items = [
      mockReceiptResponse({ id: "1", location: "Walmart", date: "2024-01-15", taxAmount: 5.25 }),
      mockReceiptResponse({ id: "2", location: "Chipotle", date: "2024-01-20", taxAmount: 1.50 }),
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

    const { useReceipts } = await import("@/hooks/useReceipts");
    vi.mocked(useReceipts).mockReturnValue(mockQueryResult({
      data: items,
      total: items.length,
      isLoading: false,
    }));

    renderWithProviders(<Receipts />);
    await user.click(screen.getByLabelText("Select all rows"));

    // Selecting all rows reveals the bulk-actions bar with the count
    // ("2 receipts selected") and a Delete button.
    const bar = await screen.findByRole("region", { name: "Bulk actions" });
    expect(bar).toHaveTextContent(/2.* receipts selected/);
    expect(
      screen.getByRole("button", { name: /^delete$/i }),
    ).toBeInTheDocument();
  });

  it("renders YNAB sync status badges when data is available", async () => {
    const items = [
      mockReceiptResponse({ id: "r1", location: "Walmart", date: "2024-01-15", taxAmount: 5.25 }),
      mockReceiptResponse({ id: "r2", location: "Target", date: "2024-01-20", taxAmount: 3.50 }),
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

    const { useReceipts } = await import("@/hooks/useReceipts");
    vi.mocked(useReceipts).mockReturnValue(mockQueryResult({
      data: items,
      total: items.length,
      isLoading: false,
    }));

    const { useReceiptYnabSyncStatuses } = await import("@/hooks/useYnab");
    vi.mocked(useReceiptYnabSyncStatuses).mockReturnValue(mockQueryResult({
      statusMap: new Map([
        ["r1", "Synced"],
        ["r2", "Failed"],
      ]),
    }));

    renderWithProviders(<Receipts />);
    expect(screen.getAllByText("YNAB").length).toBeGreaterThan(0);
    expect(screen.getByText("Error")).toBeInTheDocument();
  });

  it("renders the combined Status column header", async () => {
    const items = [
      mockReceiptResponse({ id: "1", location: "Walmart", date: "2024-01-15", taxAmount: 5.25 }),
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

    const { useReceipts } = await import("@/hooks/useReceipts");
    vi.mocked(useReceipts).mockReturnValue(mockQueryResult({
      data: items,
      total: items.length,
      isLoading: false,
    }));

    renderWithProviders(<Receipts />);
    expect(screen.getByRole("columnheader", { name: "Status" })).toBeInTheDocument();
  });

  it("opens create dialog on shortcut:new-item event", async () => {
    const { act } = await import("@testing-library/react");
    renderWithProviders(<Receipts />);

    act(() => {
      window.dispatchEvent(new Event("shortcut:new-item"));
    });

    await screen.findByRole("heading", { name: /create receipt/i });
    expect(
      screen.getByRole("heading", { name: /create receipt/i }),
    ).toBeInTheDocument();
  });

  // RECEIPTS-784: a failed query must render an error state (with retry), not
  // the "No receipts yet" empty state.
  it("renders an ErrorState with a working Retry button when the query fails", async () => {
    const user = (await import("@testing-library/user-event")).default.setup();
    const refetch = vi.fn();
    const { useReceipts } = await import("@/hooks/useReceipts");
    vi.mocked(useReceipts).mockReturnValue(
      mockQueryResult({
        data: [],
        total: 0,
        isLoading: false,
        isError: true,
        isRefetching: false,
        refetch,
      }),
    );

    renderWithProviders(<Receipts />);

    expect(screen.getByText(/couldn't load receipts/i)).toBeInTheDocument();
    expect(screen.queryByText(/no receipts yet/i)).not.toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: /try again/i }));
    expect(refetch).toHaveBeenCalled();
  });

  // RECEIPTS-784 (regression guard): a background refetch that errors while
  // rows are already cached must NOT blank the list with the ErrorState — the
  // data stays on screen (the global toast surfaces the transient failure).
  it("keeps the list visible when a background refetch fails but data is cached", async () => {
    const items = [
      mockReceiptResponse({ id: "1", location: "Walmart", date: "2024-01-15", taxAmount: 5.25 }),
      mockReceiptResponse({ id: "2", location: "Target", date: "2024-01-20", taxAmount: 3.5 }),
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

    const { useReceipts } = await import("@/hooks/useReceipts");
    vi.mocked(useReceipts).mockReturnValue(
      mockQueryResult({
        data: items,
        total: items.length,
        isLoading: false,
        // Refetch-after-success failure: error is set but cached rows remain.
        isError: true,
        isRefetching: true,
        refetch: vi.fn(),
      }),
    );

    renderWithProviders(<Receipts />);

    expect(screen.getByText("Walmart")).toBeInTheDocument();
    expect(screen.getByText("Target")).toBeInTheDocument();
    expect(screen.queryByText(/couldn't load receipts/i)).not.toBeInTheDocument();
  });

  // RECEIPTS-841: drill-down from Spending by Location forwards an
  // exact-match `location` filter to the receipts list.
  it("forwards the location URL param to useReceipts and shows the filtered alert", async () => {
    const { useReceipts } = await import("@/hooks/useReceipts");
    const mockUseReceipts = vi.mocked(useReceipts);
    mockUseReceipts.mockReturnValue(
      mockQueryResult({ data: [], total: 0, isLoading: false }),
    );

    renderWithProviders(<Receipts />, { route: "/?location=Target" });

    expect(mockUseReceipts).toHaveBeenLastCalledWith(
      0,
      25,
      "date",
      "desc",
      undefined,
      undefined,
      null,
      expect.objectContaining({ location: "Target" }),
    );

    const alert = screen.getByText(/Filtered to receipts at/i);
    expect(alert.textContent).toContain("Target");
  });

  it("clears the location filter (and resets the page) when Clear filter is clicked", async () => {
    const user = (await import("@testing-library/user-event")).default.setup();
    const { useReceipts } = await import("@/hooks/useReceipts");
    vi.mocked(useReceipts).mockReturnValue(
      mockQueryResult({ data: [], total: 0, isLoading: false }),
    );

    const { useServerPagination } = await import("@/hooks/useServerPagination");
    const resetPage = vi.fn();
    vi.mocked(useServerPagination).mockReturnValue({
      offset: 0,
      limit: 25,
      currentPage: 1,
      pageSize: 25,
      totalPages: vi.fn(() => 1),
      setPage: vi.fn(),
      setPageSize: vi.fn(),
      resetPage,
    });

    renderWithProviders(<Receipts />, { route: "/?location=Target" });

    expect(screen.getByText(/Filtered to receipts at/i)).toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: /clear filter/i }));

    expect(resetPage).toHaveBeenCalled();
    await vi.waitFor(() => {
      expect(
        screen.queryByText(/Filtered to receipts at/i),
      ).not.toBeInTheDocument();
    });
  });

  // RECEIPTS-783: after a partial bulk YNAB push, only the succeeded receipts
  // are deselected — failed ones stay selected so the user can retry.
  it("keeps failed receipts selected after a partial bulk YNAB push", async () => {
    const user = (await import("@testing-library/user-event")).default.setup();
    const items = [
      mockReceiptResponse({ id: "1", location: "Walmart", date: "2024-01-15", taxAmount: 5.25 }),
      mockReceiptResponse({ id: "2", location: "Target", date: "2024-01-20", taxAmount: 3.5 }),
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

    const { useReceipts } = await import("@/hooks/useReceipts");
    vi.mocked(useReceipts).mockReturnValue(mockQueryResult({
      data: items,
      total: items.length,
      isLoading: false,
    }));

    const bulkMutate = vi.fn(
      (_ids: string[], opts?: { onSuccess?: (data: unknown) => void }) => {
        opts?.onSuccess?.({
          results: [
            { receiptId: "1", result: { success: true, pushedTransactions: [] } },
            { receiptId: "2", result: { success: false, pushedTransactions: [], error: "nope" } },
          ],
        });
      },
    );
    const { useBulkPushYnabTransactions } = await import("@/hooks/useYnab");
    vi.mocked(useBulkPushYnabTransactions).mockReturnValue(
      mockMutationResult({ mutate: bulkMutate, isPending: false }),
    );

    renderWithProviders(<Receipts />);

    await user.click(screen.getByLabelText("Select all rows"));
    const bar = await screen.findByRole("region", { name: "Bulk actions" });
    expect(bar).toHaveTextContent(/2.*receipts selected/);

    await user.click(screen.getByRole("button", { name: /push to ynab/i }));

    expect(bulkMutate).toHaveBeenCalledWith(["1", "2"], expect.any(Object));
    // r1 pushed (cleared); r2 failed (still selected) → "1 of 2 receipt selected".
    expect(
      screen.getByRole("region", { name: "Bulk actions" }),
    ).toHaveTextContent(/1 of 2 receipt selected/);
  });
});
