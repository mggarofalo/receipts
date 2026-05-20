import { screen } from "@testing-library/react";
import { renderWithProviders } from "@/test/test-utils";
import { mockQueryResult, mockMutationResult } from "@/test/mock-hooks";
import RecycleBin from "./RecycleBin";

vi.mock("@/hooks/usePageTitle", () => ({
  usePageTitle: vi.fn(),
}));

vi.mock("@/hooks/useReceipts", () => ({
  useDeletedReceipts: vi.fn(() => ({ data: [], total: 0, isLoading: false })),
  useRestoreReceipt: vi.fn(() => ({ mutate: vi.fn(), isPending: false })),
}));

vi.mock("@/hooks/useReceiptItems", () => ({
  useDeletedReceiptItems: vi.fn(() => ({ data: [], total: 0, isLoading: false })),
  useRestoreReceiptItem: vi.fn(() => ({ mutate: vi.fn(), isPending: false })),
}));

vi.mock("@/hooks/useTransactions", () => ({
  useDeletedTransactions: vi.fn(() => ({ data: [], total: 0, isLoading: false })),
  useRestoreTransaction: vi.fn(() => ({ mutate: vi.fn(), isPending: false })),
}));

vi.mock("@/hooks/useCategories", () => ({
  useDeletedCategories: vi.fn(() => ({ data: [], total: 0, isLoading: false })),
  useRestoreCategory: vi.fn(() => ({ mutate: vi.fn(), isPending: false })),
}));

vi.mock("@/hooks/useSubcategories", () => ({
  useDeletedSubcategories: vi.fn(() => ({ data: [], total: 0, isLoading: false })),
  useRestoreSubcategory: vi.fn(() => ({ mutate: vi.fn(), isPending: false })),
}));

vi.mock("@/hooks/useItemTemplates", () => ({
  useDeletedItemTemplates: vi.fn(() => ({ data: [], total: 0, isLoading: false })),
  useRestoreItemTemplate: vi.fn(() => ({ mutate: vi.fn(), isPending: false })),
}));

vi.mock("@/hooks/useTrash", () => ({
  usePurgeTrash: vi.fn(() => ({ mutate: vi.fn(), isPending: false })),
}));

vi.mock("@/hooks/useListKeyboardNav", () => ({
  useListKeyboardNav: vi.fn(() => ({
    focusedId: null,
    focusedIndex: -1,
    setFocusedIndex: vi.fn(),
    tableRef: { current: null },
    containerProps: { role: "grid" as const, tabIndex: 0, "aria-label": "list", "aria-activedescendant": undefined },
    getRowProps: (id: string) => ({ id: `list-row-${id}`, role: "row" as const }),
  })),
}));

describe("RecycleBin", () => {
  beforeEach(async () => {
    const { useDeletedReceipts, useRestoreReceipt } = await import("@/hooks/useReceipts");
    vi.mocked(useDeletedReceipts).mockReturnValue(mockQueryResult({
      data: [],
      total: 0,
      isLoading: false,
    }));
    vi.mocked(useRestoreReceipt).mockReturnValue(mockMutationResult());
  });

  it("renders the page heading", () => {
    renderWithProviders(<RecycleBin />);
    expect(
      screen.getByRole("heading", { name: /^trash$/i }),
    ).toBeInTheDocument();
  });

  it("renders loading skeleton when data is loading", async () => {
    const { useDeletedReceipts } = await import("@/hooks/useReceipts");
    vi.mocked(useDeletedReceipts).mockReturnValue(mockQueryResult({
      data: undefined,
      isLoading: true,
    }));

    const { container } = renderWithProviders(<RecycleBin />);
    expect(container.querySelector("[data-slot='skeleton']")).toBeInTheDocument();
  });

  it("renders the All tab", () => {
    renderWithProviders(<RecycleBin />);
    expect(
      screen.getByRole("tab", { name: /all/i }),
    ).toBeInTheDocument();
  });

  it("renders the Empty Trash button", () => {
    renderWithProviders(<RecycleBin />);
    expect(
      screen.getByRole("button", { name: /empty trash/i }),
    ).toBeInTheDocument();
  });

  it("renders empty state when no deleted items exist", () => {
    renderWithProviders(<RecycleBin />);
    expect(
      screen.getByText(/no deleted items found/i),
    ).toBeInTheDocument();
  });

  it("renders deleted items in the table when data exists", async () => {
    const { useDeletedReceipts } = await import("@/hooks/useReceipts");
    vi.mocked(useDeletedReceipts).mockReturnValue(mockQueryResult({
      data: [{ id: "r1", location: "Store", date: "2026-01-01" }],
      total: 1,
      isLoading: false,
    }));

    const { useDeletedItemTemplates } = await import("@/hooks/useItemTemplates");
    vi.mocked(useDeletedItemTemplates).mockReturnValue(mockQueryResult({
      data: [{ id: "it1", name: "Deleted Template" }],
      total: 1,
      isLoading: false,
    }));

    renderWithProviders(<RecycleBin />);
    expect(screen.getByText("Receipt")).toBeInTheDocument();
    expect(screen.getByText("Item Template")).toBeInTheDocument();
    expect(screen.getByText("Store - 2026-01-01")).toBeInTheDocument();
    expect(screen.getByText("Deleted Template")).toBeInTheDocument();
  });

  it("renders entity type tabs when deleted items exist", async () => {
    const { useDeletedReceipts } = await import("@/hooks/useReceipts");
    vi.mocked(useDeletedReceipts).mockReturnValue(mockQueryResult({
      data: [{ id: "r1", location: "Store", date: "2026-01-01" }],
      total: 1,
      isLoading: false,
    }));

    renderWithProviders(<RecycleBin />);
    // Should have an "All" tab and a "Receipt" tab
    expect(screen.getByRole("tab", { name: /all/i })).toBeInTheDocument();
    expect(screen.getByRole("tab", { name: /receipt/i })).toBeInTheDocument();
  });

  it("calls restoreReceipt.mutate when Restore is clicked on a receipt", async () => {
    const user = (await import("@testing-library/user-event")).default.setup();
    const mockMutate = vi.fn();
    const { useDeletedReceipts, useRestoreReceipt } = await import("@/hooks/useReceipts");
    vi.mocked(useDeletedReceipts).mockReturnValue(mockQueryResult({
      data: [{ id: "r1", location: "Store", date: "2026-01-01" }],
      total: 1,
      isLoading: false,
    }));
    vi.mocked(useRestoreReceipt).mockReturnValue(mockMutationResult({ mutate: mockMutate }));

    renderWithProviders(<RecycleBin />);
    const restoreButtons = screen.getAllByRole("button", { name: /restore/i });
    await user.click(restoreButtons[0]);

    expect(mockMutate).toHaveBeenCalledWith("r1");
  });

  it("calls restoreTransaction.mutate when Restore is clicked on a transaction", async () => {
    const user = (await import("@testing-library/user-event")).default.setup();
    const mockMutate = vi.fn();
    const { useDeletedTransactions, useRestoreTransaction } = await import("@/hooks/useTransactions");
    vi.mocked(useDeletedTransactions).mockReturnValue(mockQueryResult({
      data: [{ id: "t1", amount: 25.50, date: "2026-01-01" }],
      total: 1,
      isLoading: false,
    }));
    vi.mocked(useRestoreTransaction).mockReturnValue(mockMutationResult({ mutate: mockMutate }));

    renderWithProviders(<RecycleBin />);
    const restoreButtons = screen.getAllByRole("button", { name: /restore/i });
    await user.click(restoreButtons[0]);

    expect(mockMutate).toHaveBeenCalledWith("t1");
  });

  it("calls purgeTrash.mutate when Empty Trash is confirmed", async () => {
    const user = (await import("@testing-library/user-event")).default.setup();
    const mockMutate = vi.fn();
    const { usePurgeTrash } = await import("@/hooks/useTrash");
    vi.mocked(usePurgeTrash).mockReturnValue(mockMutationResult({ mutate: mockMutate }));

    const { useDeletedReceipts } = await import("@/hooks/useReceipts");
    vi.mocked(useDeletedReceipts).mockReturnValue(mockQueryResult({
      data: [{ id: "r1", location: "Store", date: "2026-01-01" }],
      total: 1,
      isLoading: false,
    }));

    renderWithProviders(<RecycleBin />);
    await user.click(screen.getByRole("button", { name: /empty trash/i }));

    expect(screen.getByRole("heading", { name: /empty trash\?/i })).toBeInTheDocument();

    const confirmButtons = screen.getAllByRole("button", { name: /empty trash/i });
    const confirmButton = confirmButtons.find(
      (btn) => btn.closest("[role='alertdialog']") !== null,
    );
    await user.click(confirmButton!);

    expect(mockMutate).toHaveBeenCalled();
  });

  it("renders deleted transaction items with amount and date", async () => {
    const { useDeletedTransactions } = await import("@/hooks/useTransactions");
    vi.mocked(useDeletedTransactions).mockReturnValue(mockQueryResult({
      data: [{ id: "t1", amount: 42.50, date: "2026-03-15" }],
      total: 1,
      isLoading: false,
    }));

    renderWithProviders(<RecycleBin />);
    expect(screen.getByText("$42.50 - 2026-03-15")).toBeInTheDocument();
    expect(screen.getByText("Transaction")).toBeInTheDocument();
  });

  it("renders deleted receipt items with receipt item code", async () => {
    const { useDeletedReceiptItems } = await import("@/hooks/useReceiptItems");
    vi.mocked(useDeletedReceiptItems).mockReturnValue(mockQueryResult({
      data: [{ id: "ri1", description: "Widget", receiptItemCode: "W-001" }],
      total: 1,
      isLoading: false,
    }));

    renderWithProviders(<RecycleBin />);
    expect(screen.getByText("Widget (W-001)")).toBeInTheDocument();
  });

  it("shows filtered items when entity type tab is clicked", async () => {
    const user = (await import("@testing-library/user-event")).default.setup();
    const { useDeletedReceipts } = await import("@/hooks/useReceipts");
    vi.mocked(useDeletedReceipts).mockReturnValue(mockQueryResult({
      data: [{ id: "r1", location: "Store", date: "2026-01-01" }],
      total: 1,
      isLoading: false,
    }));

    const { useDeletedItemTemplates } = await import("@/hooks/useItemTemplates");
    vi.mocked(useDeletedItemTemplates).mockReturnValue(mockQueryResult({
      data: [{ id: "it1", name: "Template" }],
      total: 1,
      isLoading: false,
    }));

    renderWithProviders(<RecycleBin />);

    // Click the Item Template tab (use exact match to avoid ambiguity with Receipt/Receipt Item)
    const tabs = screen.getAllByRole("tab");
    const templateTab = tabs.find((t) => t.textContent?.includes("Item Template"));
    expect(templateTab).toBeDefined();
    await user.click(templateTab!);

    // Template should be visible in the tab content
    expect(screen.getByText("Template")).toBeInTheDocument();
  });

  it("renders receipt item with null code as N/A", async () => {
    const { useDeletedReceiptItems } = await import("@/hooks/useReceiptItems");
    vi.mocked(useDeletedReceiptItems).mockReturnValue(mockQueryResult({
      data: [{ id: "ri1", description: "No Code Item", receiptItemCode: null }],
      total: 1,
      isLoading: false,
    }));

    renderWithProviders(<RecycleBin />);
    expect(screen.getByText("No Code Item (N/A)")).toBeInTheDocument();
  });

  it("calls setFocusedIndex when row is clicked in All tab", async () => {
    const user = (await import("@testing-library/user-event")).default.setup();
    const mockSetFocusedIndex = vi.fn();
    const { useListKeyboardNav } = await import("@/hooks/useListKeyboardNav");
    vi.mocked(useListKeyboardNav).mockReturnValue({
      focusedId: null,
      focusedIndex: -1,
      setFocusedIndex: mockSetFocusedIndex,
      tableRef: { current: null },
      containerProps: { role: "grid" as const, tabIndex: 0, "aria-label": "list", "aria-activedescendant": undefined },
      getRowProps: (id: string) => ({ id: `list-row-${id}`, role: "row" as const }),
    });

    const { useDeletedReceipts } = await import("@/hooks/useReceipts");
    vi.mocked(useDeletedReceipts).mockReturnValue(mockQueryResult({
      data: [{ id: "r1", location: "Click Store", date: "2026-01-01" }],
      total: 1,
      isLoading: false,
    }));

    renderWithProviders(<RecycleBin />);
    await user.click(screen.getByText("Click Store - 2026-01-01"));

    expect(mockSetFocusedIndex).toHaveBeenCalledWith(0);
  });

  it("highlights focused row with bg-accent class", async () => {
    const { useListKeyboardNav } = await import("@/hooks/useListKeyboardNav");
    vi.mocked(useListKeyboardNav).mockReturnValue({
      focusedId: "Receipt:r1",
      focusedIndex: 0,
      setFocusedIndex: vi.fn(),
      tableRef: { current: null },
      containerProps: { role: "grid" as const, tabIndex: 0, "aria-label": "list", "aria-activedescendant": undefined },
      getRowProps: (id: string) => ({ id: `list-row-${id}`, role: "row" as const }),
    });

    const { useDeletedReceipts } = await import("@/hooks/useReceipts");
    vi.mocked(useDeletedReceipts).mockReturnValue(mockQueryResult({
      data: [{ id: "r1", location: "Focused Store", date: "2026-01-01" }],
      total: 1,
      isLoading: false,
    }));

    renderWithProviders(<RecycleBin />);
    const row = screen.getByText("Focused Store - 2026-01-01").closest("tr");
    expect(row?.className).toContain("bg-accent");
  });

  it("shows Emptying state when purge is pending", async () => {
    const { usePurgeTrash } = await import("@/hooks/useTrash");
    vi.mocked(usePurgeTrash).mockReturnValue(mockMutationResult({ isPending: true }));

    const { useDeletedReceipts } = await import("@/hooks/useReceipts");
    vi.mocked(useDeletedReceipts).mockReturnValue(mockQueryResult({
      data: [{ id: "r1", location: "Store", date: "2026-01-01" }],
      total: 1,
      isLoading: false,
    }));

    renderWithProviders(<RecycleBin />);
    expect(screen.getByText(/emptying/i)).toBeInTheDocument();
  });
});
