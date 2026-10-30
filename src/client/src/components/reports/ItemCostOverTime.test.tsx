import { screen, waitFor } from "@testing-library/react";
import "@/test/setup-combobox-polyfills";
import userEvent from "@testing-library/user-event";
import { renderWithQueryClient } from "@/test/test-utils";
import ItemCostOverTime from "./ItemCostOverTime";

vi.mock("@/hooks/useItemCostOverTime", () => ({
  useItemDescriptions: vi.fn(),
  useItemCostOverTime: vi.fn(),
}));
vi.mock("@/hooks/useCategoryTaxonomy", () => ({
  useCategoryTaxonomy: () => ({
    data: {
      id: "revision",
      nodes: [
        {
          id: "11111111-1111-1111-1111-111111111111",
          name: "Food",
          archived: false,
        },
        {
          id: "22222222-2222-2222-2222-222222222222",
          parentId: "11111111-1111-1111-1111-111111111111",
          name: "Dairy",
          archived: false,
        },
      ],
    },
    isLoading: false,
    isError: false,
  }),
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
  AreaTimeChart: ({
    trendlineData,
    data,
    formatValue,
    "aria-label": label,
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
  }: any) => (
    <div
      data-testid="area-chart"
      data-has-trendline={trendlineData ? "true" : "false"}
      aria-label={label}
    >
      {data
        .map(
          (point: { period: string; amount: number }) =>
            `${point.period}: ${formatValue(point.amount)}`,
        )
        .join(", ")}
    </div>
  ),
}));

import {
  useItemDescriptions,
  useItemCostOverTime,
} from "@/hooks/useItemCostOverTime";
import {
  chooseCategoryOption,
  findCategoryOption,
} from "@/test/category-picker";
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
        { period: "2023-01", amount: 1.5, currency: "USD" },
        { period: "2023-02", amount: 1.75, currency: "USD" },
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
      screen.getByText(/choose an item or category above/i),
    ).toBeInTheDocument();
    expect(screen.queryByTestId("chart-card")).not.toBeInTheDocument();
  });

  it("reads the selected item and category from the URL on load", () => {
    setupMocks();
    renderWithQueryClient(<ItemCostOverTime />, {
      route: "/?item=Milk&category=Dairy",
    });

    expect(screen.getByRole("heading", { name: "Milk" })).toBeInTheDocument();
    expect(mockCostHook).toHaveBeenLastCalledWith(
      expect.objectContaining({
        description: "Milk",
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
        category: "Dairy",
      }),
    );
  });

  it("loads stable category URLs with hierarchy, cutoff, and captured revision", () => {
    setupMocks();
    renderWithQueryClient(<ItemCostOverTime />, {
      route:
        "/?categoryId=22222222-2222-2222-2222-222222222222&categoryView=currentHierarchy&knowledgeCutoff=2026-01-01T00%3A00%3A00.000Z&capturedCurrentRevisionId=33333333-3333-3333-3333-333333333333&granularity=monthly",
    });
    expect(screen.getByText("Category: Food / Dairy")).toBeInTheDocument();
    expect(mockCostHook).toHaveBeenLastCalledWith(
      expect.objectContaining({
        categoryId: "22222222-2222-2222-2222-222222222222",
        categoryView: "currentHierarchy",
        knowledgeCutoff: "2026-01-01T00:00:00.000Z",
        capturedCurrentRevisionId: "33333333-3333-3333-3333-333333333333",
        granularity: "monthly",
      }),
    );
  });

  it("keeps unlike currencies in separate labeled charts", () => {
    setupMocks({
      data: {
        buckets: [
          { period: "2026-01", amount: 2, currency: "USD" },
          { period: "2026-01", amount: 3, currency: "EUR" },
        ],
      },
    });
    renderWithQueryClient(<ItemCostOverTime />, {
      route: "/?item=Milk&category=Dairy",
    });
    expect(screen.getAllByTestId("area-chart")).toHaveLength(2);
    expect(screen.getByLabelText("Milk · USD")).toHaveTextContent("$2.00");
    expect(screen.getByLabelText("Milk · EUR")).toHaveTextContent("€3.00");
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

    expect(screen.getByRole("heading", { name: "Milk" })).toBeInTheDocument();
    expect(
      screen.getByRole("button", { name: "Each purchase" }),
    ).toHaveAttribute("data-variant", "default");
    expect(screen.getByTestId("area-chart")).toHaveAttribute(
      "data-has-trendline",
      "false",
    );
  });

  it("selects a nested category by ID and clears the selected item", async () => {
    const user = userEvent.setup();
    setupMocks();
    renderWithQueryClient(<ItemCostOverTime />, {
      route: "/?item=Milk&category=Dairy",
    });

    expect(screen.getByRole("heading", { name: "Milk" })).toBeInTheDocument();

    await user.click(screen.getByRole("combobox", { name: "Category path" }));
    await chooseCategoryOption(user, "Food / Dairy");

    expect(screen.getByText("Category: Food / Dairy")).toBeInTheDocument();
    expect(
      screen.queryByRole("heading", { name: "Milk" }),
    ).not.toBeInTheDocument();
    expect(mockCostHook).toHaveBeenLastCalledWith(
      expect.objectContaining({
        categoryId: "22222222-2222-2222-2222-222222222222",
      }),
    );
  });

  it("does not offer an Unclassified filter that the report cannot query", async () => {
    const user = userEvent.setup();
    setupMocks();
    renderWithQueryClient(<ItemCostOverTime />);

    await user.click(screen.getByRole("combobox", { name: "Category path" }));
    expect(
      screen.queryByRole("option", { name: "Unclassified" }),
    ).not.toBeInTheDocument();
    expect(await findCategoryOption(user, "Food / Dairy")).toBeInTheDocument();
    await user.clear(
      screen.getByRole("combobox", { name: "Search category paths" }),
    );
    await user.type(
      screen.getByRole("combobox", { name: "Search category paths" }),
      "Unclassified",
    );
    expect(
      screen.queryByRole("option", { name: "Unclassified" }),
    ).not.toBeInTheDocument();
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

  // RECEIPTS-841: drill-down from Spending by Normalized Description.
  describe("normalized-description drill-down", () => {
    it("pre-selects the chart from a ?normalized= URL and queries by normalizedDescription only", () => {
      setupMocks();
      renderWithQueryClient(<ItemCostOverTime />, {
        route: "/?normalized=Organic%20Milk",
      });

      expect(
        screen.getByRole("heading", { name: "Normalized: Organic Milk" }),
      ).toBeInTheDocument();
      expect(
        screen.getByRole("combobox", { name: "Search items" }),
      ).toHaveTextContent("Organic Milk");

      const lastCall = mockCostHook.mock.calls.at(-1)?.[0];
      expect(lastCall).toMatchObject({
        normalizedDescription: "Organic Milk",
        granularity: "exact",
      });
      expect(lastCall).not.toHaveProperty("description");
      expect(lastCall).not.toHaveProperty("category");
    });

    it("clears the normalized drill-down when picking an item from the search box", async () => {
      const user = userEvent.setup();
      setupMocks();
      renderWithQueryClient(<ItemCostOverTime />, {
        route: "/?normalized=Organic%20Milk",
      });

      expect(
        screen.getByRole("heading", { name: "Normalized: Organic Milk" }),
      ).toBeInTheDocument();

      mockDescriptionsHook.mockReturnValue({
        data: {
          items: [{ description: "Milk", category: "Dairy", occurrences: 5 }],
        },
        isLoading: false,
        // eslint-disable-next-line @typescript-eslint/no-explicit-any
      } as any);

      await user.click(screen.getByRole("combobox", { name: "Search items" }));
      await user.type(
        screen.getByPlaceholderText("Type to search items..."),
        "Mi",
      );

      await waitFor(() => {
        expect(screen.getByText("Milk")).toBeInTheDocument();
      });
      await user.click(screen.getByText("Milk"));

      expect(
        screen.queryByRole("heading", { name: /^normalized:/i }),
      ).not.toBeInTheDocument();
      expect(screen.getByRole("heading", { name: "Milk" })).toBeInTheDocument();
      const lastCall = mockCostHook.mock.calls.at(-1)?.[0];
      expect(lastCall).toMatchObject({ description: "Milk" });
      expect(lastCall).not.toHaveProperty("normalizedDescription");
    });

    it("clears the normalized drill-down when choosing a category path", async () => {
      const user = userEvent.setup();
      setupMocks();
      renderWithQueryClient(<ItemCostOverTime />, {
        route: "/?normalized=Organic%20Milk",
      });

      expect(
        screen.getByRole("heading", { name: "Normalized: Organic Milk" }),
      ).toBeInTheDocument();

      await user.click(screen.getByRole("combobox", { name: "Category path" }));
      await chooseCategoryOption(user, "Food / Dairy");

      expect(
        screen.queryByRole("heading", { name: /^normalized:/i }),
      ).not.toBeInTheDocument();
      expect(screen.getByText("Category: Food / Dairy")).toBeInTheDocument();
      const lastCall = mockCostHook.mock.calls.at(-1)?.[0];
      expect(lastCall).not.toHaveProperty("normalizedDescription");
      expect(lastCall).toMatchObject({
        categoryId: "22222222-2222-2222-2222-222222222222",
      });
    });
  });
});
