import { screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { renderWithQueryClient } from "@/test/test-utils";
import { useNavigate, useLocation } from "react-router";
import { mockQueryResult } from "@/test/mock-hooks";
import CategoryTrends from "./CategoryTrends";

const { exportCsv } = vi.hoisted(() => ({ exportCsv: vi.fn() }));
vi.mock("@/hooks/useCsvExport", () => ({
  useCsvExport: () => ({ exportCsv, isExporting: false }),
}));
function Navigation() {
  const navigate = useNavigate();
  const location = useLocation();
  return (
    <>
      <output data-testid="url">{location.search}</output>
      <button
        onClick={() =>
          navigate(
            "?categoryView=currentHierarchy&report=category-trends&unrelated=keep",
          )
        }
      >
        Shared link
      </button>
      <button onClick={() => navigate(-1)}>Back</button>
      <button onClick={() => navigate(1)}>Forward</button>
    </>
  );
}

vi.mock("@/hooks/useCategoryTrendsReport", () => ({
  useCategoryTrendsReport: vi.fn(),
}));

const ENVELOPE_ID = "33333333-3333-3333-3333-333333333333";
const OCCASION_ID = "44444444-4444-4444-4444-444444444444";
vi.mock("@/hooks/useEnvelopes", () => {
  const envelopes = [
    {
      id: "33333333-3333-3333-3333-333333333333",
      name: "Groceries",
      isActive: true,
    },
  ];
  const occasions = [
    {
      id: "44444444-4444-4444-4444-444444444444",
      name: "Birthday",
      isActive: false,
    },
  ];
  return {
    useEnvelopes: () => ({
      ...mockQueryResult({ data: { data: envelopes }, isLoading: false }),
      envelopes,
    }),
    useOccasions: () => ({
      ...mockQueryResult({ data: { data: occasions }, isLoading: false }),
      occasions,
    }),
  };
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
      view: "asClassified",
      rollup: "assignedCategory",
      assignmentSelection: "currentPointer",
      series: [
        {
          currency: "USD",
          state: "classified",
          isOther: false,
          purchaseCount: 2,
          categoryId: "cat-1",
          pathRevisionId: "rev-1",
          path: [{ categoryId: "cat-1", name: "Groceries", archived: false }],
        },
        {
          currency: "USD",
          state: "classified",
          isOther: false,
          purchaseCount: 2,
          categoryId: "cat-2",
          pathRevisionId: "rev-1",
          path: [{ categoryId: "cat-2", name: "Dining", archived: false }],
        },
      ],
      currencyTotals: [{ currency: "USD", amount: 345, purchaseCount: 4 }],
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

  it("reads the shared category selection and clears captured revision on view change", async () => {
    setupMock();
    renderWithQueryClient(<CategoryTrends />, {
      route:
        "/?report=category-trends&categoryView=currentHierarchy&rollup=root&categoryId=11111111-1111-1111-1111-111111111111&knowledgeCutoff=2026-01-01T00:00:00Z&capturedCurrentRevisionId=22222222-2222-2222-2222-222222222222",
    });
    expect(mockHook).toHaveBeenLastCalledWith(
      expect.objectContaining({
        categoryView: "currentHierarchy",
        rollup: "root",
        categoryId: "11111111-1111-1111-1111-111111111111",
        knowledgeCutoff: "2026-01-01T00:00:00.000Z",
        capturedCurrentRevisionId: "22222222-2222-2222-2222-222222222222",
      }),
    );
    await userEvent.selectOptions(
      screen.getByLabelText("Category view"),
      "asClassified",
    );
    expect(mockHook).toHaveBeenLastCalledWith(
      expect.objectContaining({
        categoryView: "asClassified",
        capturedCurrentRevisionId: undefined,
        rollup: "root",
      }),
    );
  });

  it("narrows by envelope and occasion through the URL", async () => {
    setupMock();
    renderWithQueryClient(
      <>
        <Navigation />
        <CategoryTrends />
      </>,
      { route: `/?report=category-trends&occasionId=${OCCASION_ID}` },
    );
    expect(mockHook).toHaveBeenLastCalledWith(
      expect.objectContaining({
        occasionId: OCCASION_ID,
        envelopeId: undefined,
      }),
    );
    expect(screen.getByLabelText("Occasion filter")).toHaveValue(OCCASION_ID);
    expect(
      screen.getByRole("option", { name: "Birthday (archived)" }),
    ).toBeInTheDocument();

    await userEvent.selectOptions(
      screen.getByLabelText("Envelope filter"),
      "Groceries",
    );
    expect(mockHook).toHaveBeenLastCalledWith(
      expect.objectContaining({
        envelopeId: ENVELOPE_ID,
        occasionId: OCCASION_ID,
      }),
    );
    const url = () =>
      new URLSearchParams(screen.getByTestId("url").textContent!);
    expect(url().get("envelopeId")).toBe(ENVELOPE_ID);

    await userEvent.selectOptions(
      screen.getByLabelText("Occasion filter"),
      "All occasions",
    );
    expect(url().has("occasionId")).toBe(false);
    expect(url().get("report")).toBe("category-trends");
    expect(mockHook).toHaveBeenLastCalledWith(
      expect.objectContaining({
        envelopeId: ENVELOPE_ID,
        occasionId: undefined,
      }),
    );
  });

  it("ignores a malformed envelope id in the URL", () => {
    setupMock();
    renderWithQueryClient(<CategoryTrends />, {
      route: "/?envelopeId=not-a-uuid",
    });
    expect(mockHook).toHaveBeenLastCalledWith(
      expect.objectContaining({ envelopeId: undefined }),
    );
  });

  it("shows errors with a retry action", async () => {
    const refetch = vi.fn();
    setupMock({ data: undefined, isError: true, refetch });
    renderWithQueryClient(<CategoryTrends />);
    expect(screen.getByRole("alert")).toHaveTextContent("could not be loaded");
    await userEvent.click(screen.getByRole("button", { name: "Retry" }));
    expect(refetch).toHaveBeenCalledOnce();
    expect(screen.getByRole("button", { name: "Export CSV" })).toBeDisabled();
  });

  it("tracks shared-link navigation, back/forward and preserves unrelated URL parameters", async () => {
    setupMock();
    renderWithQueryClient(
      <>
        <Navigation />
        <CategoryTrends />
      </>,
      { route: "/?categoryView=asClassified&report=category-trends" },
    );
    await userEvent.click(screen.getByRole("button", { name: "Shared link" }));
    expect(screen.getByLabelText("Category view")).toHaveValue(
      "currentHierarchy",
    );
    await userEvent.click(screen.getByRole("button", { name: "Back" }));
    expect(screen.getByLabelText("Category view")).toHaveValue("asClassified");
    await userEvent.click(screen.getByRole("button", { name: "Forward" }));
    expect(screen.getByLabelText("Category view")).toHaveValue(
      "currentHierarchy",
    );
    await userEvent.selectOptions(
      screen.getByLabelText("Category view"),
      "asClassified",
    );
    expect(screen.getByTestId("url")).toHaveTextContent("unrelated=keep");
    expect(screen.getByTestId("url")).toHaveTextContent(
      "report=category-trends",
    );
  });

  it("captures one returned report for export even when view changes before download", async () => {
    setupMock();
    let finish: (() => void) | undefined;
    const pending = new Promise<void>((resolve) => {
      finish = resolve;
    });
    let captured: unknown;
    exportCsv.mockImplementationOnce(async (request) => {
      await pending;
      captured = request;
    });
    renderWithQueryClient(<CategoryTrends />);
    await userEvent.click(screen.getByRole("button", { name: "Export CSV" }));
    const report = mockHook.mock.results[0].value.data;
    report.buckets[0].amounts[0] = 999;
    report.view = "currentHierarchy";
    await userEvent.selectOptions(
      screen.getByLabelText("Category view"),
      "currentHierarchy",
    );
    finish!();
    await pending;
    await Promise.resolve();
    expect(captured).toEqual(
      expect.objectContaining({
        rows: expect.arrayContaining([
          expect.arrayContaining(["asClassified", 100]),
        ]),
      }),
    );
  });

  it("disables export while loading", () => {
    setupMock({ data: undefined, isLoading: true });
    renderWithQueryClient(<CategoryTrends />);
    expect(screen.getByRole("button", { name: "Export CSV" })).toBeDisabled();
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
      route:
        "/?startDate=2022-01-01&endDate=2022-06-30&granularity=quarterly&topN=10",
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
      route:
        "/?startDate=not-a-date&endDate=also-not-a-date&granularity=bogus&topN=999",
    });

    expect(screen.getByText("Category Trends")).toBeInTheDocument();
    expect(mockHook).toHaveBeenLastCalledWith(
      expect.objectContaining({ granularity: "monthly", topN: 5 }),
    );
  });
});
