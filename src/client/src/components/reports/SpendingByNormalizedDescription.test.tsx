import { screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { format, subMonths } from "date-fns";
import { renderWithQueryClient } from "@/test/test-utils";
import SpendingByNormalizedDescription from "./SpendingByNormalizedDescription";

vi.mock("@/hooks/useSpendingByNormalizedDescription", () => ({
  useSpendingByNormalizedDescription: vi.fn(),
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
import { downloadCsv } from "@/lib/export-csv";
const mockHook = vi.mocked(useSpendingByNormalizedDescription);
const mockDownloadCsv = vi.mocked(downloadCsv);

const sampleItems = [
  {
    canonicalName: "Apples",
    totalAmount: 12.5,
    currency: "USD",
    itemCount: 3,
    firstSeen: null,
    lastSeen: null,
  },
  {
    canonicalName: "Bananas",
    totalAmount: 40,
    currency: "USD",
    itemCount: 5,
    firstSeen: null,
    lastSeen: null,
  },
];

function setupMock(overrides: Record<string, unknown> = {}) {
  mockHook.mockReturnValue({
    data: { items: sampleItems },
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

  it("shows empty state when no items", () => {
    setupMock({ data: { items: [] } });
    renderWithQueryClient(<SpendingByNormalizedDescription />);
    expect(screen.getByText("No Data")).toBeInTheDocument();
  });

  it("renders items sorted by total desc", () => {
    setupMock();
    renderWithQueryClient(<SpendingByNormalizedDescription />);
    const table = screen.getByRole("table");
    const rows = table.querySelectorAll("tbody tr");
    expect(rows.length).toBe(2);
    expect(rows[0].textContent).toContain("Bananas");
    expect(rows[1].textContent).toContain("Apples");
  });

  it("formats totals as currency and sums grand total", () => {
    setupMock();
    renderWithQueryClient(<SpendingByNormalizedDescription />);
    expect(screen.getByText("$52.50")).toBeInTheDocument();
  });

  it("exports the sorted dataset as csv with the date range in the filename", async () => {
    const user = userEvent.setup();
    setupMock();
    renderWithQueryClient(<SpendingByNormalizedDescription />);

    await user.click(screen.getByRole("button", { name: "Export CSV" }));
    await waitFor(() => expect(mockDownloadCsv).toHaveBeenCalledTimes(1));

    const expectedStart = format(subMonths(new Date(), 12), "yyyy-MM-dd");
    const expectedEnd = format(new Date(), "yyyy-MM-dd");
    const [filename, csv] = mockDownloadCsv.mock.calls[0];
    expect(filename).toBe(
      `spending-by-normalized-description_${expectedStart}_${expectedEnd}.csv`,
    );
    expect(csv).toBe(
      "Canonical Name,Item Count,Total Amount,Currency\r\n" +
        "Bananas,5,40,USD\r\n" +
        "Apples,3,12.5,USD\r\n",
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
    });
  });
});
