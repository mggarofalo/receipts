import { screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { renderWithQueryClient } from "@/test/test-utils";
import CategoryTrends from "./CategoryTrends";

vi.mock("@/hooks/useCategoryTrendsReport", () => ({
  useCategoryTrendsReport: vi.fn(),
}));

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
        onChange({ startDate: "2020-01-01", endDate: "2024-12-31" })
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
    action,
  }: {
    children: React.ReactNode;
    title: string;
    action?: React.ReactNode;
  }) => (
    <div data-testid="chart-card">
      <h3>{title}</h3>
      <div data-testid="chart-card-action">{action}</div>
      {children}
    </div>
  ),
  StackedAreaChart: () => <div data-testid="stacked-area-chart" />,
}));

import { useCategoryTrendsReport } from "@/hooks/useCategoryTrendsReport";
const mockHook = vi.mocked(useCategoryTrendsReport);

function setupMock(overrides: Record<string, unknown> = {}) {
  mockHook.mockReturnValue({
    data: {
      categories: ["Groceries", "Dining"],
      buckets: [
        { period: "2025-01", amounts: [100, 50] },
        { period: "2025-02", amounts: [120, 75] },
      ],
    },
    isLoading: false,
    ...overrides,
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
  } as any);
}

describe("CategoryTrends", () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it("initializes with the shared 12-month default date range (RECEIPTS-840)", () => {
    setupMock();
    renderWithQueryClient(<CategoryTrends />);

    const params = mockHook.mock.calls[0]?.[0];
    expect(params?.startDate).toBeTypeOf("string");
    expect(params?.endDate).toBeTypeOf("string");
    expect(params?.startDate).toMatch(/^\d{4}-\d{2}-\d{2}$/);
    expect(params?.endDate).toMatch(/^\d{4}-\d{2}-\d{2}$/);
  });

  it("derives monthly granularity from the 12-month default range", () => {
    setupMock();
    renderWithQueryClient(<CategoryTrends />);

    expect(mockHook).toHaveBeenLastCalledWith(
      expect.objectContaining({ granularity: "monthly" }),
    );
  });

  it("defaults topN to 5", () => {
    setupMock();
    renderWithQueryClient(<CategoryTrends />);

    expect(mockHook).toHaveBeenLastCalledWith(
      expect.objectContaining({ topN: 5 }),
    );
  });

  it("renders the stacked area chart with data", () => {
    setupMock();
    renderWithQueryClient(<CategoryTrends />);

    expect(screen.getByTestId("stacked-area-chart")).toBeInTheDocument();
    expect(screen.getByText("Category Trends")).toBeInTheDocument();
  });

  it("updates the hook when the date range changes", async () => {
    const user = userEvent.setup();
    setupMock();
    renderWithQueryClient(<CategoryTrends />);

    await user.click(screen.getByTestId("date-range-selector"));

    expect(mockHook).toHaveBeenLastCalledWith(
      expect.objectContaining({
        startDate: "2020-01-01",
        endDate: "2024-12-31",
      }),
    );
  });

  it("resets granularity override when date range changes", async () => {
    const user = userEvent.setup();
    setupMock();
    renderWithQueryClient(<CategoryTrends />);

    await user.click(screen.getByRole("button", { name: "Month" }));
    expect(mockHook).toHaveBeenLastCalledWith(
      expect.objectContaining({ granularity: "monthly" }),
    );

    await user.click(screen.getByTestId("date-range-selector"));
    expect(mockHook).toHaveBeenLastCalledWith(
      expect.objectContaining({
        startDate: "2020-01-01",
        endDate: "2024-12-31",
        granularity: "yearly",
      }),
    );
  });

  it("applies granularity override when a granularity button is clicked", async () => {
    const user = userEvent.setup();
    setupMock();
    renderWithQueryClient(<CategoryTrends />);

    await user.click(screen.getByRole("button", { name: "Quarter" }));

    expect(mockHook).toHaveBeenLastCalledWith(
      expect.objectContaining({ granularity: "quarterly" }),
    );
  });

  it("reads date range, granularity, and topN from the URL on load", () => {
    setupMock();
    renderWithQueryClient(<CategoryTrends />, {
      route: "/?startDate=2022-01-01&endDate=2022-06-30&granularity=quarterly&topN=10",
    });

    expect(mockHook).toHaveBeenLastCalledWith(
      expect.objectContaining({
        startDate: "2022-01-01",
        endDate: "2022-06-30",
        granularity: "quarterly",
        topN: 10,
      }),
    );
  });

  it("falls back to defaults for malformed URL params instead of crashing", () => {
    setupMock();
    renderWithQueryClient(<CategoryTrends />, {
      route: "/?startDate=not-a-date&endDate=also-not-a-date&granularity=bogus&topN=999",
    });

    expect(screen.getByText("Category Trends")).toBeInTheDocument();
    expect(mockHook).toHaveBeenLastCalledWith(
      expect.objectContaining({ granularity: "monthly", topN: 5 }),
    );
  });
});
