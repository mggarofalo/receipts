import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { format, subMonths } from "date-fns";
import { renderWithQueryClient } from "@/test/test-utils";
import SpendingByLocation from "./SpendingByLocation";

vi.mock("@/hooks/useSpendingByLocationReport", () => ({
  useSpendingByLocationReport: vi.fn(),
}));

vi.mock("@/lib/api-client", () => ({
  default: { GET: vi.fn() },
}));

vi.mock("@/lib/export-csv", async (importOriginal) => {
  const actual = await importOriginal<typeof import("@/lib/export-csv")>();
  return { ...actual, downloadCsv: vi.fn() };
});

vi.mock("@/components/dashboard/DateRangeSelector", () => ({
  DateRangeSelector: ({
    onChange,
  }: {
    value: { startDate?: string; endDate?: string };
    onChange: (range: { startDate?: string; endDate?: string }) => void;
  }) => (
    <button
      data-testid="date-range-selector"
      onClick={() =>
        onChange({ startDate: "2025-01-01", endDate: "2025-12-31" })
      }
    >
      DateRangeSelector
    </button>
  ),
}));

vi.mock("@/components/charts", () => ({
  ChartCard: ({
    children,
    title,
  }: {
    children: React.ReactNode;
    title: string;
  }) => (
    <div data-testid="chart-card">
      <h3>{title}</h3>
      {children}
    </div>
  ),
  BarChart: () => <div data-testid="bar-chart" />,
}));

import { useSpendingByLocationReport } from "@/hooks/useSpendingByLocationReport";
import client from "@/lib/api-client";
import { downloadCsv } from "@/lib/export-csv";
const mockHook = vi.mocked(useSpendingByLocationReport);
const mockClient = vi.mocked(client);
const mockDownloadCsv = vi.mocked(downloadCsv);

const mockItems = [
  {
    location: "Store A",
    visits: 5,
    total: 100.5,
    averagePerVisit: 20.1,
  },
  {
    location: "Store B",
    visits: 3,
    total: 60.0,
    averagePerVisit: 20.0,
  },
];

function setupMock(overrides: Record<string, unknown> = {}) {
  mockHook.mockReturnValue({
    data: {
      totalCount: 2,
      grandTotal: 160.5,
      items: mockItems,
    },
    isLoading: false,
    isError: false,
    ...overrides,
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
  } as any);
}

describe("SpendingByLocation", () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it("shows loading skeleton", () => {
    setupMock({ isLoading: true, data: undefined });
    renderWithQueryClient(<SpendingByLocation />);
    const skeletons = document.querySelectorAll("[data-slot='skeleton']");
    expect(skeletons.length).toBeGreaterThan(0);
  });

  it("shows error state", () => {
    setupMock({ isError: true, data: undefined });
    renderWithQueryClient(<SpendingByLocation />);
    expect(
      screen.getByText(/failed to load spending by location report/i),
    ).toBeInTheDocument();
  });

  it("shows empty state when no data", () => {
    setupMock({
      data: { totalCount: 0, grandTotal: 0, items: [] },
    });
    renderWithQueryClient(<SpendingByLocation />);
    expect(screen.getByText("No Data")).toBeInTheDocument();
    expect(
      screen.getByText(/no spending data found/i),
    ).toBeInTheDocument();
  });

  it("shows empty state when data is null", () => {
    setupMock({ data: undefined });
    renderWithQueryClient(<SpendingByLocation />);
    expect(screen.getByText("No Data")).toBeInTheDocument();
  });

  it("renders summary header with count and total", () => {
    setupMock();
    renderWithQueryClient(<SpendingByLocation />);
    expect(screen.getByText("2")).toBeInTheDocument();
    expect(screen.getByText("$160.50")).toBeInTheDocument();
  });

  it("renders bar chart", () => {
    setupMock();
    renderWithQueryClient(<SpendingByLocation />);
    expect(screen.getByTestId("bar-chart")).toBeInTheDocument();
    expect(
      screen.getByText("Top Locations by Spending"),
    ).toBeInTheDocument();
  });

  it("renders table with all items", () => {
    setupMock();
    renderWithQueryClient(<SpendingByLocation />);
    expect(screen.getByText("Store A")).toBeInTheDocument();
    expect(screen.getByText("Store B")).toBeInTheDocument();
  });

  it("renders table headers", () => {
    setupMock();
    renderWithQueryClient(<SpendingByLocation />);
    const table = screen.getByRole("table");
    expect(within(table).getByText(/Location/)).toBeInTheDocument();
    expect(within(table).getByText(/Visits/)).toBeInTheDocument();
    expect(within(table).getByText(/Total/)).toBeInTheDocument();
    expect(within(table).getByText(/Avg\/Visit/)).toBeInTheDocument();
  });

  it("toggles sort direction on clicking sortable column", async () => {
    const user = userEvent.setup();
    setupMock();
    renderWithQueryClient(<SpendingByLocation />);

    // Initially sorted by total desc — find it within the table
    const table = screen.getByRole("table");
    await user.click(within(table).getByRole("button", { name: /^total/i }));

    // Should have called hook with total asc (toggled from desc)
    expect(mockHook).toHaveBeenLastCalledWith(
      expect.objectContaining({ sortBy: "total", sortDirection: "asc" }),
    );
  });

  it("switches sort column when clicking a different column", async () => {
    const user = userEvent.setup();
    setupMock();
    renderWithQueryClient(<SpendingByLocation />);

    await user.click(screen.getByRole("button", { name: /visits/i }));

    expect(mockHook).toHaveBeenLastCalledWith(
      expect.objectContaining({
        sortBy: "visits",
        sortDirection: "desc",
      }),
    );
  });

  it("sorts location column ascending by default", async () => {
    const user = userEvent.setup();
    setupMock();
    renderWithQueryClient(<SpendingByLocation />);

    const table = screen.getByRole("table");
    await user.click(within(table).getByRole("button", { name: /^location$/i }));

    expect(mockHook).toHaveBeenLastCalledWith(
      expect.objectContaining({
        sortBy: "location",
        sortDirection: "asc",
      }),
    );
  });

  it("does not show pagination when only one page", () => {
    setupMock();
    renderWithQueryClient(<SpendingByLocation />);
    expect(screen.queryByText(/Page/)).not.toBeInTheDocument();
  });

  it("shows pagination when multiple pages", () => {
    const manyItems = Array.from({ length: 51 }, (_, i) => ({
      location: `Store ${i}`,
      visits: 1,
      total: 10,
      averagePerVisit: 10,
    }));
    setupMock({
      data: { totalCount: 51, grandTotal: 510, items: manyItems },
    });
    renderWithQueryClient(<SpendingByLocation />);
    expect(screen.getByText("Page 1 of 2")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Previous" })).toBeDisabled();
    expect(
      screen.getByRole("button", { name: "Next" }),
    ).not.toBeDisabled();
  });

  it("formats currency values correctly", () => {
    setupMock();
    renderWithQueryClient(<SpendingByLocation />);
    expect(screen.getByText("Store A").closest("tr")).toHaveTextContent(
      "$100.50",
    );
  });

  it("renders date range selector", () => {
    setupMock();
    renderWithQueryClient(<SpendingByLocation />);
    expect(screen.getByTestId("date-range-selector")).toBeInTheDocument();
  });

  it("exports the full filtered dataset across pages as csv", async () => {
    const user = userEvent.setup();
    setupMock();

    mockClient.GET.mockImplementation((async (
      _path: string,
      options: { params: { query: { page: number } } },
    ) => {
      const page = options.params.query.page;
      const count = page === 1 ? 100 : 50;
      const items = Array.from({ length: count }, (_, i) => ({
        location: `Store ${(page - 1) * 100 + i}`,
        visits: 1,
        total: 10,
        averagePerVisit: 10,
      }));
      return {
        data: { totalCount: 150, grandTotal: 1500, items },
        error: undefined,
        response: {} as Response,
      };
      // eslint-disable-next-line @typescript-eslint/no-explicit-any
    }) as any);

    renderWithQueryClient(<SpendingByLocation />);

    await user.click(screen.getByRole("button", { name: "Export CSV" }));
    await waitFor(() => expect(mockDownloadCsv).toHaveBeenCalledTimes(1));

    const expectedStart = format(subMonths(new Date(), 1), "yyyy-MM-dd");
    const expectedEnd = format(new Date(), "yyyy-MM-dd");

    // Fetches every page with the current filters at the max page size.
    expect(mockClient.GET).toHaveBeenCalledTimes(2);
    expect(mockClient.GET).toHaveBeenNthCalledWith(
      1,
      "/api/reports/spending-by-location",
      {
        params: {
          query: {
            startDate: expectedStart,
            endDate: expectedEnd,
            sortBy: "total",
            sortDirection: "desc",
            page: 1,
            pageSize: 100,
          },
        },
      },
    );
    expect(mockClient.GET).toHaveBeenNthCalledWith(
      2,
      "/api/reports/spending-by-location",
      {
        params: {
          query: expect.objectContaining({ page: 2, pageSize: 100 }),
        },
      },
    );

    const [filename, csv] = mockDownloadCsv.mock.calls[0];
    expect(filename).toBe(
      `spending-by-location_${expectedStart}_${expectedEnd}.csv`,
    );
    const lines = csv.split("\r\n");
    expect(lines[0]).toBe("Location,Visits,Total,Average Per Visit");
    expect(lines[1]).toBe("Store 0,1,10,10");
    expect(lines[150]).toBe("Store 149,1,10,10");
    // Header + 150 rows + trailing empty line from the final CRLF.
    expect(lines).toHaveLength(152);
  });

  it("resets page on date range change", async () => {
    const user = userEvent.setup();
    const manyItems = Array.from({ length: 51 }, (_, i) => ({
      location: `Store ${i}`,
      visits: 1,
      total: 10,
      averagePerVisit: 10,
    }));
    setupMock({
      data: { totalCount: 51, grandTotal: 510, items: manyItems },
    });
    renderWithQueryClient(<SpendingByLocation />);

    // Go to page 2
    const nextBtn = screen.getByRole("button", { name: "Next" });
    await user.click(nextBtn);

    // Change date range
    const dateSelector = screen.getByTestId("date-range-selector");
    await user.click(dateSelector);

    // Should reset to page 1
    expect(mockHook).toHaveBeenLastCalledWith(
      expect.objectContaining({ page: 1 }),
    );
  });
});
