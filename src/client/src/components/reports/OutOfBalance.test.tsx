import { screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { renderWithQueryClient } from "@/test/test-utils";
import OutOfBalance from "./OutOfBalance";

const mockNavigate = vi.fn();
vi.mock("react-router", async () => {
  const actual = await vi.importActual("react-router");
  return { ...actual, useNavigate: () => mockNavigate };
});

vi.mock("@/hooks/useOutOfBalanceReport", () => ({
  useOutOfBalanceReport: vi.fn(),
}));

import { useOutOfBalanceReport } from "@/hooks/useOutOfBalanceReport";
const mockHook = vi.mocked(useOutOfBalanceReport);

const mockItems = [
  {
    receiptId: "id-1",
    location: "Store A",
    date: "2025-03-01",
    itemSubtotal: 10,
    taxAmount: 1,
    adjustmentTotal: 0,
    expectedTotal: 11,
    transactionTotal: 15,
    difference: -4,
  },
  {
    receiptId: "id-2",
    location: "Store B",
    date: "2025-03-02",
    itemSubtotal: 20,
    taxAmount: 2,
    adjustmentTotal: 1,
    expectedTotal: 23,
    transactionTotal: 20,
    difference: 3,
  },
];

function setupMock(overrides: Record<string, unknown> = {}) {
  mockHook.mockReturnValue({
    data: {
      totalCount: 2,
      totalDiscrepancy: 7,
      items: mockItems,
    },
    isLoading: false,
    isError: false,
    ...overrides,
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
  } as any);
}

describe("OutOfBalance", () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it("shows loading skeleton", () => {
    setupMock({ isLoading: true, data: undefined });
    renderWithQueryClient(<OutOfBalance />);
    const skeletons = document.querySelectorAll("[data-slot='skeleton']");
    expect(skeletons.length).toBeGreaterThan(0);
  });

  it("shows error state", () => {
    setupMock({ isError: true, data: undefined });
    renderWithQueryClient(<OutOfBalance />);
    expect(
      screen.getByText(/failed to load out-of-balance report/i),
    ).toBeInTheDocument();
  });

  it("shows empty state when no discrepancies", () => {
    setupMock({
      data: { totalCount: 0, totalDiscrepancy: 0, items: [] },
    });
    renderWithQueryClient(<OutOfBalance />);
    expect(screen.getByText("All Balanced")).toBeInTheDocument();
    expect(
      screen.getByText(/all receipts are balanced/i),
    ).toBeInTheDocument();
  });

  it("shows empty state when data is null", () => {
    setupMock({ data: undefined });
    renderWithQueryClient(<OutOfBalance />);
    expect(screen.getByText("All Balanced")).toBeInTheDocument();
  });

  it("renders summary header with count and discrepancy", () => {
    setupMock();
    renderWithQueryClient(<OutOfBalance />);
    expect(screen.getByText("2")).toBeInTheDocument();
    expect(screen.getByText("$7.00")).toBeInTheDocument();
  });

  it("renders table with all items", () => {
    setupMock();
    renderWithQueryClient(<OutOfBalance />);
    expect(screen.getByText("Store A")).toBeInTheDocument();
    expect(screen.getByText("Store B")).toBeInTheDocument();
  });

  it("renders table headers", () => {
    setupMock();
    renderWithQueryClient(<OutOfBalance />);
    const table = screen.getByRole("table");
    expect(within(table).getByText(/Date/)).toBeInTheDocument();
    expect(within(table).getByText("Location")).toBeInTheDocument();
    expect(within(table).getByText("Item Total")).toBeInTheDocument();
    expect(within(table).getByText("Tax")).toBeInTheDocument();
    expect(within(table).getByText("Adjustments")).toBeInTheDocument();
    expect(within(table).getByText("Expected Total")).toBeInTheDocument();
    expect(within(table).getByText("Actual Total")).toBeInTheDocument();
    expect(within(table).getByText(/Difference/)).toBeInTheDocument();
  });

  it("navigates to receipt on row click", async () => {
    const user = userEvent.setup();
    setupMock();
    renderWithQueryClient(<OutOfBalance />);

    const row = screen.getByText("Store A").closest("tr")!;
    await user.click(row);
    expect(mockNavigate).toHaveBeenCalledWith("/receipts/id-1");
  });

  it("applies negative-token color to negative difference", () => {
    setupMock();
    renderWithQueryClient(<OutOfBalance />);
    // -$4.00 should have the negative design-token color
    const negativeDiff = screen.getByText("-$4.00");
    expect(negativeDiff.getAttribute("style")).toContain("var(--neg-ink)");
  });

  it("applies warn-token color to positive difference", () => {
    setupMock();
    renderWithQueryClient(<OutOfBalance />);
    // $3.00 should have the warn design-token color
    const positiveDiff = screen.getByText("$3.00");
    expect(positiveDiff.getAttribute("style")).toContain("var(--warn-ink)");
  });

  it("toggles sort direction on clicking sortable column", async () => {
    const user = userEvent.setup();
    setupMock();
    renderWithQueryClient(<OutOfBalance />);

    // Initially sorted by date asc
    const dateHeader = screen
      .getByText(/Date/)
      .closest("th")!;
    await user.click(dateHeader);

    // Should have called hook with date desc
    expect(mockHook).toHaveBeenLastCalledWith(
      expect.objectContaining({ sortBy: "date", sortDirection: "desc" }),
    );
  });

  it("switches sort column when clicking a different column", async () => {
    const user = userEvent.setup();
    setupMock();
    renderWithQueryClient(<OutOfBalance />);

    const diffHeader = screen
      .getByText(/Difference/)
      .closest("th")!;
    await user.click(diffHeader);

    expect(mockHook).toHaveBeenLastCalledWith(
      expect.objectContaining({
        sortBy: "difference",
        sortDirection: "asc",
      }),
    );
  });

  it("does not show pagination when only one page", () => {
    setupMock();
    renderWithQueryClient(<OutOfBalance />);
    expect(screen.queryByText(/Page/)).not.toBeInTheDocument();
  });

  it("shows pagination when multiple pages", () => {
    const manyItems = Array.from({ length: 51 }, (_, i) => ({
      receiptId: `id-${i}`,
      location: `Store ${i}`,
      date: "2025-03-01",
      itemSubtotal: 10,
      taxAmount: 1,
      adjustmentTotal: 0,
      expectedTotal: 11,
      transactionTotal: 15,
      difference: -4,
    }));
    setupMock({
      data: { totalCount: 51, totalDiscrepancy: 204, items: manyItems },
    });
    renderWithQueryClient(<OutOfBalance />);
    expect(screen.getByText("Page 1 of 2")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Previous" })).toBeDisabled();
    expect(
      screen.getByRole("button", { name: "Next" }),
    ).not.toBeDisabled();
  });

  it("formats currency values correctly", () => {
    setupMock();
    renderWithQueryClient(<OutOfBalance />);
    // $10.00 item subtotal for Store A
    expect(screen.getByText("Store A").closest("tr")).toHaveTextContent(
      "$10.00",
    );
  });
});
