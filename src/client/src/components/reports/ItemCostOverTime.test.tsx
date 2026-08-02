import { screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { renderWithQueryClient } from "@/test/test-utils";
import ItemCostOverTime from "./ItemCostOverTime";

vi.mock("@/hooks/useItemCostOverTime", () => ({
  useItemDescriptions: vi.fn(),
  useItemCostOverTime: vi.fn(),
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
        onChange({ startDate: "2023-01-01", endDate: "2023-06-30" })
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
    subtitle,
    action,
    empty,
    emptyMessage,
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
  }: any) => (
    <div data-testid="chart-card">
      <h3>{title}</h3>
      {subtitle && <p data-testid="chart-subtitle">{subtitle}</p>}
      <div data-testid="chart-card-action">{action}</div>
      {empty ? <p>{emptyMessage}</p> : children}
    </div>
  ),
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  AreaTimeChart: ({ trendlineData }: any) => (
    <div
      data-testid="area-chart"
      data-has-trendline={trendlineData ? "true" : "false"}
    />
  ),
}));

import {
  useItemDescriptions,
  useItemCostOverTime,
} from "@/hooks/useItemCostOverTime";
const mockDescriptionsHook = vi.mocked(useItemDescriptions);
const mockCostHook = vi.mocked(useItemCostOverTime);

function setupMocks(overrides: Record<string, unknown> = {}) {
  mockDescriptionsHook.mockReturnValue({
    data: { items: [] },
    isLoading: false,
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
  } as any);
  mockCostHook.mockReturnValue({
    data: {
      buckets: [
        { period: "2023-01", amount: 1.5 },
        { period: "2023-02", amount: 1.75 },
      ],
    },
    isLoading: false,
    ...overrides,
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
  } as any);
}

describe("ItemCostOverTime", () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it("shows a placeholder when no item is selected", () => {
    setupMocks();
    renderWithQueryClient(<ItemCostOverTime />);

    expect(screen.getByText("Item Cost Over Time")).toBeInTheDocument();
    expect(
      screen.getByText(/search for an item above/i),
    ).toBeInTheDocument();
    expect(screen.queryByTestId("chart-card")).not.toBeInTheDocument();
  });

  it("reads the selected item and category from the URL on load", () => {
    setupMocks();
    renderWithQueryClient(<ItemCostOverTime />, {
      route: "/?item=Milk&category=Dairy",
    });

    expect(
      screen.getByRole("heading", { name: "Milk" }),
    ).toBeInTheDocument();
    expect(mockCostHook).toHaveBeenLastCalledWith(
      expect.objectContaining({
        description: "Milk",
        category: undefined,
      }),
    );
  });

  it("renders category-only mode from the URL", () => {
    setupMocks();
    renderWithQueryClient(<ItemCostOverTime />, {
      route: "/?item=Milk&category=Dairy&categoryOnly=true",
    });

    expect(screen.getByText("Category: Dairy")).toBeInTheDocument();
    expect(mockCostHook).toHaveBeenLastCalledWith(
      expect.objectContaining({
        description: undefined,
        category: "Dairy",
      }),
    );
  });

  it("falls back to no selection when category is missing (malformed item params)", () => {
    setupMocks();
    renderWithQueryClient(<ItemCostOverTime />, {
      route: "/?item=Milk",
    });

    expect(screen.getByText("Item Cost Over Time")).toBeInTheDocument();
    expect(screen.queryByTestId("chart-card")).not.toBeInTheDocument();
  });

  it("falls back to defaults for malformed range, granularity, and window size", () => {
    setupMocks();
    renderWithQueryClient(<ItemCostOverTime />, {
      route:
        "/?item=Milk&category=Dairy&startDate=garbage&endDate=garbage&granularity=bogus&trendline=maybe&windowSize=99",
    });

    expect(
      screen.getByRole("heading", { name: "Milk" }),
    ).toBeInTheDocument();
    expect(
      screen.getByRole("button", { name: "Each purchase" }),
    ).toHaveAttribute("data-variant", "default");
    expect(screen.getByTestId("area-chart")).toHaveAttribute(
      "data-has-trendline",
      "false",
    );
  });

  it("toggles category-only mode and clears the selected item", async () => {
    const user = userEvent.setup();
    setupMocks();
    renderWithQueryClient(<ItemCostOverTime />, {
      route: "/?item=Milk&category=Dairy",
    });

    expect(
      screen.getByRole("heading", { name: "Milk" }),
    ).toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: "Category" }));

    expect(screen.getByText("Item Cost Over Time")).toBeInTheDocument();
    expect(
      screen.queryByRole("heading", { name: "Milk" }),
    ).not.toBeInTheDocument();
    expect(
      screen.getByRole("button", { name: "Category" }),
    ).toHaveAttribute("data-variant", "default");
  });

  it("changes granularity via button click", async () => {
    const user = userEvent.setup();
    setupMocks();
    renderWithQueryClient(<ItemCostOverTime />, {
      route: "/?item=Milk&category=Dairy",
    });

    await user.click(screen.getByRole("button", { name: "Monthly" }));

    expect(mockCostHook).toHaveBeenLastCalledWith(
      expect.objectContaining({ granularity: "monthly" }),
    );
    expect(screen.getByRole("button", { name: "Monthly" })).toHaveAttribute(
      "data-variant",
      "default",
    );
  });

  it("toggles the trendline and reveals the window size selector", async () => {
    const user = userEvent.setup();
    setupMocks();
    renderWithQueryClient(<ItemCostOverTime />, {
      route: "/?item=Milk&category=Dairy",
    });

    expect(screen.getByTestId("area-chart")).toHaveAttribute(
      "data-has-trendline",
      "false",
    );
    expect(
      screen.queryByLabelText("Rolling average window size"),
    ).not.toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: "Trendline" }));

    expect(screen.getByTestId("area-chart")).toHaveAttribute(
      "data-has-trendline",
      "true",
    );
    expect(
      screen.getByLabelText("Rolling average window size"),
    ).toBeInTheDocument();
  });

  it("changes the date range via the date range selector", async () => {
    const user = userEvent.setup();
    setupMocks();
    renderWithQueryClient(<ItemCostOverTime />, {
      route: "/?item=Milk&category=Dairy",
    });

    await user.click(screen.getByTestId("date-range-selector"));

    expect(mockCostHook).toHaveBeenLastCalledWith(
      expect.objectContaining({
        startDate: "2023-01-01",
        endDate: "2023-06-30",
      }),
    );
  });

  it("treats the 'all' sentinel as an open-ended range", () => {
    setupMocks();
    renderWithQueryClient(<ItemCostOverTime />, {
      route: "/?item=Milk&category=Dairy&startDate=all",
    });

    expect(mockCostHook).toHaveBeenLastCalledWith(
      expect.objectContaining({ startDate: undefined, endDate: undefined }),
    );
  });
});
