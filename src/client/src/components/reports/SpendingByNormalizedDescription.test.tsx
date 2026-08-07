import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { format, subMonths } from "date-fns";
import { renderWithQueryClient } from "@/test/test-utils";
import SpendingByNormalizedDescription from "./SpendingByNormalizedDescription";

vi.mock("@/hooks/useSpendingByNormalizedDescription", () => ({
  useSpendingByNormalizedDescription: vi.fn(),
}));

vi.mock("@/lib/api-client", () => ({
  default: { GET: vi.fn() },
}));

vi.mock("@/components/dashboard/DateRangeSelector", () => ({
  DateRangeSelector: () => <div data-testid="date-range-selector" />,
}));

vi.mock("@/components/charts", () => ({
  ChartCard: ({ children }: { children: React.ReactNode }) => (
    <div data-testid="chart-card">{children}</div>
  ),
  BarChart: () => <div data-testid="bar-chart" />,
}));

vi.mock("@/lib/export-csv", async (importOriginal) => {
  const actual = await importOriginal<typeof import("@/lib/export-csv")>();
  return { ...actual, downloadCsv: vi.fn() };
});

import { useSpendingByNormalizedDescription } from "@/hooks/useSpendingByNormalizedDescription";
import client from "@/lib/api-client";
import { downloadCsv } from "@/lib/export-csv";
const mockHook = vi.mocked(useSpendingByNormalizedDescription);
const mockClient = vi.mocked(client);
const mockDownloadCsv = vi.mocked(downloadCsv);

// Deliberately NOT pre-sorted by totalAmount: Apples ($12.50) is listed
// before Bananas ($40) even though a desc-by-total sort would put Bananas
// first. This lets tests prove the component renders the server's row order
// verbatim instead of re-sorting client-side.
const sampleItems = [
  {
    canonicalName: "Apples",
    totalAmount: 12.5,
    currency: "USD",
    itemCount: 3,
    firstSeen: null,
    lastSeen: null,
    status: "active",
  },
  {
    canonicalName: "Bananas",
    totalAmount: 40,
    currency: "USD",
    itemCount: 5,
    firstSeen: null,
    lastSeen: null,
    status: "active",
  },
];

const ADMIN_AUTH = {
  user: {
    id: "u1",
    email: "admin@example.com",
    firstName: "A",
    lastName: "D",
    roles: ["Admin"],
  },
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
} as any;

function setupMock(overrides: Record<string, unknown> = {}) {
  mockHook.mockReturnValue({
    data: { items: sampleItems, totalCount: sampleItems.length, grandTotal: 52.5 },
    isLoading: false,
    isError: false,
    ...overrides,
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
  } as any);
}

describe("SpendingByNormalizedDescription", () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it("shows loading skeleton", () => {
    setupMock({ isLoading: true, data: undefined });
    renderWithQueryClient(<SpendingByNormalizedDescription />);
    const skeletons = document.querySelectorAll("[data-slot='skeleton']");
    expect(skeletons.length).toBeGreaterThan(0);
  });

  it("shows error state", () => {
    setupMock({ isError: true, data: undefined });
    renderWithQueryClient(<SpendingByNormalizedDescription />);
    expect(
      screen.getByText(/failed to load spending by normalized description/i),
    ).toBeInTheDocument();
  });

  it("shows empty state when totalCount is 0", () => {
    setupMock({ data: { items: [], totalCount: 0, grandTotal: 0 } });
    renderWithQueryClient(<SpendingByNormalizedDescription />);
    expect(screen.getByText("No Data")).toBeInTheDocument();
  });

  it("requests server-side sorting by total desc by default and renders rows in the server's exact order", () => {
    setupMock();
    renderWithQueryClient(<SpendingByNormalizedDescription />);

    expect(mockHook).toHaveBeenLastCalledWith({
      from: format(subMonths(new Date(), 12), "yyyy-MM-dd"),
      to: format(new Date(), "yyyy-MM-dd"),
      sortBy: "totalAmount",
      sortDirection: "desc",
      page: 1,
      pageSize: 50,
    });

    // Sorting is the server's job now — the component must not re-sort.
    // sampleItems is [Apples, Bananas] in that exact (non-total-desc) order.
    const table = screen.getByRole("table");
    const rows = table.querySelectorAll("tbody tr");
    expect(rows.length).toBe(2);
    expect(rows[0].textContent).toContain("Apples");
    expect(rows[1].textContent).toContain("Bananas");
  });

  it("shows the grand total reported by the server (not summed from the current page)", () => {
    setupMock();
    renderWithQueryClient(<SpendingByNormalizedDescription />);
    expect(screen.getByText("$52.50")).toBeInTheDocument();
  });

  it("renders the Share column as a percentage of the grand total", () => {
    setupMock();
    renderWithQueryClient(<SpendingByNormalizedDescription />);
    const table = screen.getByRole("table");
    const rows = table.querySelectorAll("tbody tr");
    // Apples: 12.5 / 52.5 = 23.8%; Bananas: 40 / 52.5 = 76.2%.
    expect(rows[0].textContent).toContain("23.8%");
    expect(rows[1].textContent).toContain("76.2%");
  });

  it("computes Share from the grand total, not the current page's own sum", () => {
    // If Share were computed from the page's own total, this single-row page
    // would read 100%. Because it's computed off the server's grandTotal
    // (100), it must read 12.5%.
    setupMock({
      data: {
        items: [
          { canonicalName: "Apples", totalAmount: 12.5, currency: "USD", itemCount: 3 },
        ],
        totalCount: 1,
        grandTotal: 100,
      },
    });
    renderWithQueryClient(<SpendingByNormalizedDescription />);
    const table = screen.getByRole("table");
    expect(within(table).getByText("12.5%")).toBeInTheDocument();
  });

  it("shows an em dash for Share when the grand total is 0", () => {
    setupMock({
      data: {
        items: [
          { canonicalName: "Apples", totalAmount: 0, currency: "USD", itemCount: 1 },
        ],
        totalCount: 1,
        grandTotal: 0,
      },
    });
    renderWithQueryClient(<SpendingByNormalizedDescription />);
    const table = screen.getByRole("table");
    expect(within(table).getByText("—")).toBeInTheDocument();
  });

  it("switches the active sort column and defaults canonicalName to ascending", async () => {
    const user = userEvent.setup();
    setupMock();
    renderWithQueryClient(<SpendingByNormalizedDescription />);

    const table = screen.getByRole("table");
    await user.click(
      within(table).getByRole("button", { name: "Canonical Name" }),
    );

    expect(mockHook).toHaveBeenLastCalledWith(
      expect.objectContaining({
        sortBy: "canonicalName",
        sortDirection: "asc",
        page: 1,
      }),
    );
  });

  it("switches the active sort column and defaults other columns to descending", async () => {
    const user = userEvent.setup();
    setupMock();
    renderWithQueryClient(<SpendingByNormalizedDescription />);

    await user.click(screen.getByRole("button", { name: "Items" }));

    expect(mockHook).toHaveBeenLastCalledWith(
      expect.objectContaining({
        sortBy: "itemCount",
        sortDirection: "desc",
        page: 1,
      }),
    );
  });

  it("toggles direction when clicking the already-active sort column", async () => {
    const user = userEvent.setup();
    setupMock();
    renderWithQueryClient(<SpendingByNormalizedDescription />);

    const table = screen.getByRole("table");
    await user.click(within(table).getByRole("button", { name: /^total/i }));

    expect(mockHook).toHaveBeenLastCalledWith(
      expect.objectContaining({
        sortBy: "totalAmount",
        sortDirection: "asc",
        page: 1,
      }),
    );
  });

  it("resets page to 1 when switching the sort column", async () => {
    const user = userEvent.setup();
    setupMock();
    renderWithQueryClient(<SpendingByNormalizedDescription />, {
      route: "/?page=2",
    });

    await user.click(screen.getByRole("button", { name: "Items" }));

    expect(mockHook).toHaveBeenLastCalledWith(
      expect.objectContaining({ page: 1 }),
    );
  });

  it("does not show pagination when only one page", () => {
    setupMock();
    renderWithQueryClient(<SpendingByNormalizedDescription />);
    expect(screen.queryByText(/Page/)).not.toBeInTheDocument();
  });

  it("shows pagination and disables Previous on the first page", () => {
    const manyItems = Array.from({ length: 51 }, (_, i) => ({
      canonicalName: `Item ${i}`,
      totalAmount: 10,
      currency: "USD",
      itemCount: 1,
    }));
    setupMock({ data: { items: manyItems, totalCount: 51, grandTotal: 510 } });
    renderWithQueryClient(<SpendingByNormalizedDescription />);

    expect(screen.getByText("Page 1 of 2")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Previous" })).toBeDisabled();
    expect(screen.getByRole("button", { name: "Next" })).not.toBeDisabled();
  });

  it("disables Next on the last page", () => {
    const manyItems = Array.from({ length: 51 }, (_, i) => ({
      canonicalName: `Item ${i}`,
      totalAmount: 10,
      currency: "USD",
      itemCount: 1,
    }));
    setupMock({ data: { items: manyItems, totalCount: 51, grandTotal: 510 } });
    renderWithQueryClient(<SpendingByNormalizedDescription />, {
      route: "/?page=2",
    });

    expect(screen.getByText("Page 2 of 2")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Next" })).toBeDisabled();
    expect(screen.getByRole("button", { name: "Previous" })).not.toBeDisabled();
  });

  it("updates the page param when clicking Next and Previous", async () => {
    const user = userEvent.setup();
    const manyItems = Array.from({ length: 51 }, (_, i) => ({
      canonicalName: `Item ${i}`,
      totalAmount: 10,
      currency: "USD",
      itemCount: 1,
    }));
    setupMock({ data: { items: manyItems, totalCount: 51, grandTotal: 510 } });
    renderWithQueryClient(<SpendingByNormalizedDescription />);

    await user.click(screen.getByRole("button", { name: "Next" }));
    expect(mockHook).toHaveBeenLastCalledWith(
      expect.objectContaining({ page: 2 }),
    );

    await user.click(screen.getByRole("button", { name: "Previous" }));
    expect(mockHook).toHaveBeenLastCalledWith(
      expect.objectContaining({ page: 1 }),
    );
  });

  it("links the canonical name cell to the Item Cost Over Time drill-down carrying the current date range", () => {
    setupMock();
    renderWithQueryClient(<SpendingByNormalizedDescription />, {
      route: "/?startDate=2023-01-01&endDate=2023-06-30",
    });

    const link = screen.getByRole("link", { name: "Apples" });
    expect(link).toHaveAttribute(
      "href",
      "/reports?report=item-cost-over-time&normalized=Apples&startDate=2023-01-01&endDate=2023-06-30",
    );
  });

  it("renders the (Not Normalized) row as plain text with no link", () => {
    setupMock({
      data: {
        items: [
          {
            canonicalName: "(Not Normalized)",
            totalAmount: 5,
            currency: "USD",
            itemCount: 1,
          },
        ],
        totalCount: 1,
        grandTotal: 5,
      },
    });
    renderWithQueryClient(<SpendingByNormalizedDescription />);

    expect(screen.getByText("(Not Normalized)")).toBeInTheDocument();
    expect(
      screen.queryByRole("link", { name: "(Not Normalized)" }),
    ).not.toBeInTheDocument();
  });

  it("exports all pages as csv, including the Share of Total column", async () => {
    const user = userEvent.setup();
    setupMock();

    mockClient.GET.mockResolvedValue({
      data: { items: sampleItems, totalCount: sampleItems.length },
      error: undefined,
      response: {} as Response,
      // eslint-disable-next-line @typescript-eslint/no-explicit-any
    } as any);

    renderWithQueryClient(<SpendingByNormalizedDescription />);

    await user.click(screen.getByRole("button", { name: "Export CSV" }));
    await waitFor(() => expect(mockDownloadCsv).toHaveBeenCalledTimes(1));

    const expectedStart = format(subMonths(new Date(), 12), "yyyy-MM-dd");
    const expectedEnd = format(new Date(), "yyyy-MM-dd");

    expect(mockClient.GET).toHaveBeenCalledWith(
      "/api/reports/spending-by-normalized-description",
      {
        params: {
          query: {
            from: expectedStart,
            to: expectedEnd,
            sortBy: "totalAmount",
            sortDirection: "desc",
            page: 1,
            pageSize: 100,
          },
        },
      },
    );

    const [filename, csv] = mockDownloadCsv.mock.calls[0];
    expect(filename).toBe(
      `spending-by-normalized-description_${expectedStart}_${expectedEnd}.csv`,
    );
    expect(csv).toBe(
      "Canonical Name,Item Count,Total Amount,Share of Total,Currency,Review Status\r\n" +
        "Apples,3,12.5,23.8%,USD,Reviewed\r\n" +
        "Bananas,5,40,76.2%,USD,Reviewed\r\n",
    );
  });

  it("reads the date range from the URL on load", () => {
    setupMock();
    renderWithQueryClient(<SpendingByNormalizedDescription />, {
      route: "/?startDate=2023-01-01&endDate=2023-06-30",
    });

    expect(mockHook).toHaveBeenLastCalledWith({
      from: "2023-01-01",
      to: "2023-06-30",
      sortBy: "totalAmount",
      sortDirection: "desc",
      page: 1,
      pageSize: 50,
    });
  });

  it("treats the 'all' sentinel as an open-ended range", () => {
    setupMock();
    renderWithQueryClient(<SpendingByNormalizedDescription />, {
      route: "/?startDate=all",
    });

    expect(mockHook).toHaveBeenLastCalledWith({
      from: undefined,
      to: undefined,
      sortBy: "totalAmount",
      sortDirection: "desc",
      page: 1,
      pageSize: 50,
    });
  });

  it("falls back to the default range for malformed URL params instead of crashing", () => {
    setupMock();
    renderWithQueryClient(<SpendingByNormalizedDescription />, {
      route: "/?startDate=not-a-date&endDate=2023-06-30",
    });

    expect(screen.getByRole("table")).toBeInTheDocument();
    const expectedStart = format(subMonths(new Date(), 12), "yyyy-MM-dd");
    const expectedEnd = format(new Date(), "yyyy-MM-dd");
    expect(mockHook).toHaveBeenLastCalledWith({
      from: expectedStart,
      to: expectedEnd,
      sortBy: "totalAmount",
      sortDirection: "desc",
      page: 1,
      pageSize: 50,
    });
  });

  it("reads sort and page params from the URL on load and forwards them to the API", () => {
    setupMock();
    renderWithQueryClient(<SpendingByNormalizedDescription />, {
      route: "/?sortBy=canonicalName&sortDirection=asc&page=3",
    });

    expect(mockHook).toHaveBeenLastCalledWith(
      expect.objectContaining({
        sortBy: "canonicalName",
        sortDirection: "asc",
        page: 3,
      }),
    );
  });

  // RECEIPTS-875: the report is the place approval becomes visible. A bucket nobody has
  // confirmed has to read as provisional — while still counting toward the totals, so the
  // report keeps reconciling against receipt totals.
  describe("unreviewed buckets", () => {
    const withPending = [
      sampleItems[0],
      { ...sampleItems[1], status: "pendingReview" },
    ];

    it("badges a pending bucket and leaves an active one unbadged", () => {
      setupMock({
        data: { items: withPending, totalCount: 2, grandTotal: 52.5 },
      });
      renderWithQueryClient(<SpendingByNormalizedDescription />);

      const bananas = screen.getByText("Bananas").closest("tr")!;
      expect(within(bananas).getByTestId("unreviewed-badge")).toBeInTheDocument();

      const apples = screen.getByText("Apples").closest("tr")!;
      expect(within(apples).queryByTestId("unreviewed-badge")).toBeNull();
    });

    // The API serializes this enum PascalCase despite the spec documenting it lowercase
    // (RECEIPTS-884). If the badge only matched the documented casing it would silently
    // never render against the real server.
    it("recognises the PascalCase casing the API actually sends", () => {
      setupMock({
        data: {
          items: [sampleItems[0], { ...sampleItems[1], status: "PendingReview" }],
          totalCount: 2,
          grandTotal: 52.5,
        },
      });
      renderWithQueryClient(<SpendingByNormalizedDescription />);

      const bananas = screen.getByText("Bananas").closest("tr")!;
      expect(within(bananas).getByTestId("unreviewed-badge")).toBeInTheDocument();
    });

    it("keeps pending spend in the totals", () => {
      setupMock({
        data: { items: withPending, totalCount: 2, grandTotal: 52.5 },
      });
      renderWithQueryClient(<SpendingByNormalizedDescription />);

      // 52.50 is Apples + Bananas. If pending spend were excluded this would read $12.50.
      expect(screen.getByText("$52.50")).toBeInTheDocument();
    });

    it("explains the badge, and offers the review link only to an admin", () => {
      setupMock({
        data: { items: withPending, totalCount: 2, grandTotal: 52.5 },
      });
      const { unmount } = renderWithQueryClient(
        <SpendingByNormalizedDescription />,
      );

      expect(screen.getByTestId("unreviewed-note")).toHaveTextContent(
        /1 bucket on this page is still awaiting review/i,
      );
      expect(screen.queryByRole("link", { name: /review them/i })).toBeNull();
      unmount();

      renderWithQueryClient(<SpendingByNormalizedDescription />, {
        auth: ADMIN_AUTH,
      });
      expect(
        screen.getByRole("link", { name: /review them/i }),
      ).toHaveAttribute("href", "/admin/normalized-descriptions");
    });

    it("says nothing when every bucket on the page is reviewed", () => {
      setupMock();
      renderWithQueryClient(<SpendingByNormalizedDescription />);

      expect(screen.queryByTestId("unreviewed-note")).toBeNull();
      expect(screen.queryByTestId("unreviewed-badge")).toBeNull();
    });

    it("exports review status, blank for the bucket that has no backing row", async () => {
      const user = userEvent.setup();
      const exportItems = [
        sampleItems[0],
        { ...sampleItems[1], status: "PendingReview" },
        {
          canonicalName: "(Not Normalized)",
          totalAmount: 5,
          currency: "USD",
          itemCount: 1,
          firstSeen: null,
          lastSeen: null,
          status: null,
        },
      ];
      setupMock({
        data: { items: exportItems, totalCount: 3, grandTotal: 57.5 },
      });
      mockClient.GET.mockResolvedValue({
        data: { items: exportItems, totalCount: 3 },
        error: undefined,
        response: {} as Response,
        // eslint-disable-next-line @typescript-eslint/no-explicit-any
      } as any);

      renderWithQueryClient(<SpendingByNormalizedDescription />);
      await user.click(screen.getByRole("button", { name: /export csv/i }));

      await waitFor(() => expect(mockDownloadCsv).toHaveBeenCalledTimes(1));
      const [, csv] = mockDownloadCsv.mock.calls[0];
      const [header, ...rows] = csv.trimEnd().split("\r\n");

      expect(header.endsWith(",Review Status")).toBe(true);
      expect(rows[0].endsWith(",Reviewed")).toBe(true);
      expect(rows[1].endsWith(",Unreviewed")).toBe(true);
      // Trailing empty cell, not "Reviewed": there is no row behind this bucket, so nobody
      // approved anything.
      expect(rows[2].endsWith(",")).toBe(true);
    });
  });
});
