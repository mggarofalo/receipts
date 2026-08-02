import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { format } from "date-fns";
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

vi.mock("@/lib/api-client", () => ({
  default: { GET: vi.fn() },
}));

vi.mock("@/lib/export-csv", async (importOriginal) => {
  const actual = await importOriginal<typeof import("@/lib/export-csv")>();
  return { ...actual, downloadCsv: vi.fn() };
});

import { useOutOfBalanceReport } from "@/hooks/useOutOfBalanceReport";
import client from "@/lib/api-client";
import { downloadCsv } from "@/lib/export-csv";
const mockHook = vi.mocked(useOutOfBalanceReport);
const mockClient = vi.mocked(client);
const mockDownloadCsv = vi.mocked(downloadCsv);

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
    await user.click(screen.getByRole("button", { name: /date/i }));

    // Should have called hook with date desc
    expect(mockHook).toHaveBeenLastCalledWith(
      expect.objectContaining({ sortBy: "date", sortDirection: "desc" }),
    );
  });

  it("switches sort column when clicking a different column", async () => {
    const user = userEvent.setup();
    setupMock();
    renderWithQueryClient(<OutOfBalance />);

    await user.click(screen.getByRole("button", { name: /difference/i }));

    expect(mockHook).toHaveBeenLastCalledWith(
      expect.objectContaining({
        sortBy: "difference",
        sortDirection: "asc",
      }),
    );
  });

  it("sets aria-sort on the active sort column header", () => {
    setupMock();
    renderWithQueryClient(<OutOfBalance />);

    const dateHeader = screen.getByRole("button", { name: /date/i }).closest("th")!;
    expect(dateHeader).toHaveAttribute("aria-sort", "ascending");
  });

  it("navigates to receipt on View click", async () => {
    const user = userEvent.setup();
    setupMock();
    renderWithQueryClient(<OutOfBalance />);

    const viewButtons = screen.getAllByRole("button", { name: "View" });
    await user.click(viewButtons[0]);
    expect(mockNavigate).toHaveBeenCalledWith("/receipts/id-1");
  });

  it("View button is keyboard focusable", async () => {
    const user = userEvent.setup();
    setupMock();
    renderWithQueryClient(<OutOfBalance />);

    const viewButtons = screen.getAllByRole("button", { name: "View" });
    viewButtons[0].focus();
    expect(viewButtons[0]).toHaveFocus();
    await user.keyboard("{Enter}");
    expect(mockNavigate).toHaveBeenCalledWith("/receipts/id-1");
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

  it("exports the report as csv with the current sort", async () => {
    const user = userEvent.setup();
    setupMock();
    mockClient.GET.mockResolvedValue({
      data: { totalCount: 2, totalDiscrepancy: 7, items: mockItems },
      error: undefined,
      response: {} as Response,
      // eslint-disable-next-line @typescript-eslint/no-explicit-any
    } as any);

    renderWithQueryClient(<OutOfBalance />);

    await user.click(screen.getByRole("button", { name: "Export CSV" }));
    await waitFor(() => expect(mockDownloadCsv).toHaveBeenCalledTimes(1));

    expect(mockClient.GET).toHaveBeenCalledWith(
      "/api/reports/out-of-balance",
      {
        params: {
          query: {
            sortBy: "date",
            sortDirection: "asc",
            page: 1,
            pageSize: 100,
          },
        },
      },
    );

    const [filename, csv] = mockDownloadCsv.mock.calls[0];
    expect(filename).toBe(
      `out-of-balance_${format(new Date(), "yyyy-MM-dd")}.csv`,
    );
    expect(csv).toBe(
      "Date,Location,Item Subtotal,Tax,Adjustments,Expected Total,Actual Total,Difference,Receipt ID\r\n" +
        "2025-03-01,Store A,10,1,0,11,15,-4,id-1\r\n" +
        "2025-03-02,Store B,20,2,1,23,20,3,id-2\r\n",
    );
  });

  it("reads sort and page from the URL on load", () => {
    setupMock();
    renderWithQueryClient(<OutOfBalance />, {
      route: "/?sortBy=difference&sortDirection=desc&page=3",
    });

    expect(mockHook).toHaveBeenLastCalledWith(
      expect.objectContaining({
        sortBy: "difference",
        sortDirection: "desc",
        page: 3,
      }),
    );
  });

  it("falls back to defaults for malformed URL params instead of crashing", () => {
    setupMock();
    renderWithQueryClient(<OutOfBalance />, {
      route: "/?sortBy=nonsense&sortDirection=up&page=abc",
    });

    expect(screen.getByRole("table")).toBeInTheDocument();
    expect(mockHook).toHaveBeenLastCalledWith(
      expect.objectContaining({ sortBy: "date", sortDirection: "asc", page: 1 }),
    );
  });

  it("falls back to defaults for out-of-range page numbers instead of crashing", () => {
    setupMock();
    renderWithQueryClient(<OutOfBalance />, {
      route: "/?page=-1",
    });

    expect(screen.getByRole("table")).toBeInTheDocument();
    expect(mockHook).toHaveBeenLastCalledWith(
      expect.objectContaining({ page: 1 }),
    );
  });
});
