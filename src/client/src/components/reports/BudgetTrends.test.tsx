import { screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { useLocation } from "react-router";
import { renderWithQueryClient } from "@/test/test-utils";
import { mockQueryResult } from "@/test/mock-hooks";
import type { BudgetTrendsResult } from "@/lib/budget-trends";
import type { Envelope, Occasion } from "@/hooks/useEnvelopes";
import BudgetTrends from "./BudgetTrends";

// jsdom polyfills required by the Radix Select used for Top N.
beforeAll(() => {
  if (
    !(Element.prototype as unknown as { hasPointerCapture?: unknown })
      .hasPointerCapture
  ) {
    Element.prototype.hasPointerCapture = () => false;
    Element.prototype.releasePointerCapture = () => {};
    Element.prototype.setPointerCapture = () => {};
  }
  if (
    !(Element.prototype as unknown as { scrollIntoView?: unknown })
      .scrollIntoView
  ) {
    Element.prototype.scrollIntoView = () => {};
  }
});

const { exportCsv } = vi.hoisted(() => ({ exportCsv: vi.fn() }));
vi.mock("@/hooks/useCsvExport", () => ({
  useCsvExport: () => ({ exportCsv, isExporting: false }),
}));

vi.mock("@/hooks/useBudgetTrendsReport", () => ({
  useBudgetTrendsReport: vi.fn(),
}));
vi.mock("@/hooks/useEnvelopes", () => ({
  useEnvelopes: vi.fn(),
  useOccasions: vi.fn(),
}));

vi.mock("@/components/dashboard/DateRangeSelector", () => ({
  DateRangeSelector: ({
    onChange,
  }: {
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

vi.mock("@/components/charts", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@/components/charts")>()),
  StackedAreaChart: ({
    categories,
    "aria-label": label,
  }: {
    categories: string[];
    "aria-label"?: string;
  }) => (
    <ul data-testid="stacked-area-chart" aria-label={label}>
      {categories.map((category) => (
        <li key={category}>{category}</li>
      ))}
    </ul>
  ),
}));

import { useBudgetTrendsReport } from "@/hooks/useBudgetTrendsReport";
import { useEnvelopes, useOccasions } from "@/hooks/useEnvelopes";
const mockHook = vi.mocked(useBudgetTrendsReport);

const GROCERIES = "11111111-1111-1111-1111-111111111111";
const OLD_RENT = "22222222-2222-2222-2222-222222222222";
const BIRTHDAY = "33333333-3333-3333-3333-333333333333";

function report(): BudgetTrendsResult {
  return {
    groupBy: "envelope",
    series: [
      {
        id: GROCERIES,
        name: "Groceries",
        isActive: true,
        currency: "USD",
        isOther: false,
        total: 150,
        purchaseCount: 3,
      },
      {
        id: null,
        name: "No envelope",
        isActive: true,
        currency: "USD",
        isOther: false,
        total: 25.5,
        purchaseCount: 2,
      },
      {
        id: OLD_RENT,
        name: "Old rent",
        isActive: false,
        currency: "EUR",
        isOther: false,
        total: 40,
        purchaseCount: 1,
      },
    ],
    buckets: [
      { period: "2025-01", amounts: [100, 25.5, 40] },
      { period: "2025-02", amounts: [50, 0, 0] },
    ],
  };
}

function setup(overrides: Record<string, unknown> = {}) {
  mockHook.mockReturnValue(
    mockQueryResult({
      data: report(),
      isSuccess: true,
      isPending: false,
      isLoading: false,
      status: "success",
      ...overrides,
    }),
  );
  const envelopes: Envelope[] = [
    { id: GROCERIES, name: "Groceries", isActive: true },
    { id: OLD_RENT, name: "Old rent", isActive: false },
  ];
  const occasions: Occasion[] = [
    { id: BIRTHDAY, name: "Birthday", isActive: true },
  ];
  vi.mocked(useEnvelopes).mockReturnValue({
    ...mockQueryResult({ data: { data: envelopes }, isLoading: false }),
    envelopes,
  });
  vi.mocked(useOccasions).mockReturnValue({
    ...mockQueryResult({ data: { data: occasions }, isLoading: false }),
    occasions,
  });
}

function Url() {
  return <output data-testid="url">{useLocation().search}</output>;
}

function urlParams() {
  return new URLSearchParams(screen.getByTestId("url").textContent!);
}

function renderReport(route = "/?report=spending-by-envelope") {
  return renderWithQueryClient(
    <>
      <Url />
      <BudgetTrends />
    </>,
    { route },
  );
}

beforeEach(() => vi.clearAllMocks());

describe("BudgetTrends", () => {
  it("requests envelopes by default with the shared defaults", () => {
    setup();
    renderReport();
    const params = mockHook.mock.lastCall![0];
    expect(params).toEqual(
      expect.objectContaining({
        groupBy: "envelope",
        granularity: "monthly",
        topN: 7,
        envelopeId: undefined,
        occasionId: undefined,
      }),
    );
    expect(params.startDate).toMatch(/^\d{4}-\d{2}-\d{2}$/);
    expect(params.endDate).toMatch(/^\d{4}-\d{2}-\d{2}$/);
  });

  it("renders one chart and totals table per currency", () => {
    setup();
    renderReport();
    expect(screen.getByText("Spending by Envelope")).toBeInTheDocument();

    const usd = screen.getByRole("region", {
      name: "USD spending by envelope",
    });
    expect(within(usd).getByText("USD - $175.50 total")).toBeInTheDocument();
    expect(
      within(within(usd).getByTestId("stacked-area-chart"))
        .getAllByRole("listitem")
        .map((item) => item.textContent),
    ).toEqual(["Groceries", "No envelope"]);
    const usdRows = within(usd).getAllByRole("row").slice(1);
    expect(usdRows.map((row) => row.textContent)).toEqual([
      "Groceries3$150.00",
      "No envelope2$25.50",
    ]);
    expect(
      within(usd).getByRole("columnheader", { name: "Envelope" }),
    ).toBeInTheDocument();

    const eur = screen.getByRole("region", {
      name: "EUR spending by envelope",
    });
    expect(within(eur).getByText("EUR - €40.00 total")).toBeInTheDocument();
    expect(
      within(eur).getByRole("row", { name: /Old rent \(archived\)/ }),
    ).toHaveTextContent("Old rent (archived)1€40.00");
  });

  it("shows a loading state and disables export", () => {
    setup({ data: undefined, isLoading: true, isSuccess: false });
    renderReport();
    expect(
      screen.getByText("Loading…").closest("[role=status]"),
    ).toHaveAttribute("aria-busy", "true");
    expect(screen.queryByRole("region")).toBeNull();
    expect(screen.getByRole("button", { name: "Export CSV" })).toBeDisabled();
  });

  it("says when there is no spending and disables export", () => {
    setup({ data: { groupBy: "envelope", series: [], buckets: [] } });
    renderReport();
    expect(
      screen.getByText("No spending for this selection"),
    ).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Export CSV" })).toBeDisabled();
  });

  it("shows errors with a retry action", async () => {
    const refetch = vi.fn();
    setup({ data: undefined, isError: true, isSuccess: false, refetch });
    renderReport();
    expect(screen.getByRole("alert")).toHaveTextContent(
      "Spending by envelope could not be loaded.",
    );
    expect(screen.queryByText("No spending for this selection")).toBeNull();
    await userEvent.click(screen.getByRole("button", { name: "Retry" }));
    expect(refetch).toHaveBeenCalledOnce();
    expect(screen.getByRole("button", { name: "Export CSV" })).toBeDisabled();
  });

  it("switches to occasions, swapping which filter is offered", async () => {
    setup();
    renderReport();
    expect(screen.getByLabelText("Occasion filter")).toBeInTheDocument();
    expect(screen.queryByLabelText("Envelope filter")).toBeNull();

    await userEvent.selectOptions(
      screen.getByLabelText("Group by"),
      "occasion",
    );

    expect(urlParams().get("groupBy")).toBe("occasion");
    expect(urlParams().get("report")).toBe("spending-by-envelope");
    expect(mockHook).toHaveBeenLastCalledWith(
      expect.objectContaining({ groupBy: "occasion" }),
    );
    expect(screen.getByText("Spending by Occasion")).toBeInTheDocument();
    expect(
      screen.getAllByRole("columnheader", { name: "Occasion" }),
    ).toHaveLength(2);
    expect(screen.getByLabelText("Envelope filter")).toBeInTheDocument();
    expect(screen.queryByLabelText("Occasion filter")).toBeNull();
  });

  it("applies and clears a filter through the URL", async () => {
    setup();
    renderReport();
    await userEvent.selectOptions(
      screen.getByLabelText("Occasion filter"),
      "Birthday",
    );
    expect(urlParams().get("occasionId")).toBe(BIRTHDAY);
    expect(mockHook).toHaveBeenLastCalledWith(
      expect.objectContaining({ occasionId: BIRTHDAY }),
    );

    await userEvent.selectOptions(
      screen.getByLabelText("Occasion filter"),
      "All occasions",
    );
    expect(urlParams().has("occasionId")).toBe(false);
    expect(mockHook).toHaveBeenLastCalledWith(
      expect.objectContaining({ occasionId: undefined }),
    );
  });

  it("drops the envelope filter when regrouping by envelope", async () => {
    setup();
    renderReport(
      `/?groupBy=occasion&envelopeId=${GROCERIES}&occasionId=${BIRTHDAY}`,
    );

    await userEvent.selectOptions(
      screen.getByLabelText("Group by"),
      "envelope",
    );

    expect(urlParams().get("groupBy")).toBe("envelope");
    expect(urlParams().has("envelopeId")).toBe(false);
    expect(urlParams().get("occasionId")).toBe(BIRTHDAY);
    expect(mockHook).toHaveBeenLastCalledWith(
      expect.objectContaining({
        groupBy: "envelope",
        envelopeId: undefined,
        occasionId: BIRTHDAY,
      }),
    );
  });

  it("drops the occasion filter when regrouping by occasion", async () => {
    setup();
    renderReport(
      `/?groupBy=envelope&envelopeId=${GROCERIES}&occasionId=${BIRTHDAY}`,
    );

    await userEvent.selectOptions(
      screen.getByLabelText("Group by"),
      "occasion",
    );

    expect(urlParams().get("groupBy")).toBe("occasion");
    expect(urlParams().has("occasionId")).toBe(false);
    expect(urlParams().get("envelopeId")).toBe(GROCERIES);
    expect(mockHook).toHaveBeenLastCalledWith(
      expect.objectContaining({
        groupBy: "occasion",
        envelopeId: GROCERIES,
        occasionId: undefined,
      }),
    );
  });

  it("reads every selection from a shared link and ignores malformed values", () => {
    setup();
    renderReport(
      `/?groupBy=occasion&envelopeId=${OLD_RENT}&occasionId=nope&startDate=2022-01-01&endDate=2022-06-30&granularity=quarterly&topN=10`,
    );
    expect(mockHook).toHaveBeenLastCalledWith({
      groupBy: "occasion",
      envelopeId: OLD_RENT,
      occasionId: undefined,
      startDate: "2022-01-01",
      endDate: "2022-06-30",
      granularity: "quarterly",
      topN: 10,
    });
    expect(screen.getByLabelText("Envelope filter")).toHaveValue(OLD_RENT);
  });

  it.each([
    {
      groupBy: "envelope",
      expected: { envelopeId: undefined, occasionId: BIRTHDAY },
    },
    {
      groupBy: "occasion",
      expected: { envelopeId: GROCERIES, occasionId: undefined },
    },
  ])(
    "ignores a shared link's filter on the grouped dimension ($groupBy)",
    ({ groupBy, expected }) => {
      setup();
      renderReport(
        `/?groupBy=${groupBy}&envelopeId=${GROCERIES}&occasionId=${BIRTHDAY}`,
      );
      expect(mockHook).toHaveBeenLastCalledWith(
        expect.objectContaining({ groupBy, ...expected }),
      );
    },
  );

  it("falls back to defaults for unknown grouping, granularity and topN", () => {
    setup();
    renderReport("/?groupBy=bogus&granularity=bogus&topN=999");
    expect(mockHook).toHaveBeenLastCalledWith(
      expect.objectContaining({
        groupBy: "envelope",
        granularity: "monthly",
        topN: 7,
      }),
    );
  });

  it("overrides granularity and resets it when the date range changes", async () => {
    setup();
    renderReport();
    await userEvent.click(screen.getByRole("button", { name: "Quarter" }));
    expect(urlParams().get("granularity")).toBe("quarterly");
    expect(mockHook).toHaveBeenLastCalledWith(
      expect.objectContaining({ granularity: "quarterly" }),
    );

    await userEvent.click(screen.getByTestId("date-range-selector"));
    expect(urlParams().has("granularity")).toBe(false);
    expect(mockHook).toHaveBeenLastCalledWith(
      expect.objectContaining({
        startDate: "2020-01-01",
        endDate: "2024-12-31",
        granularity: "yearly",
      }),
    );
  });

  it("changes how many series are shown per currency", async () => {
    setup();
    renderReport();
    await userEvent.click(
      screen.getByRole("combobox", { name: "Top N envelopes" }),
    );
    await userEvent.click(screen.getByRole("option", { name: "Top 10" }));
    expect(urlParams().get("topN")).toBe("10");
    expect(mockHook).toHaveBeenLastCalledWith(
      expect.objectContaining({ topN: 10 }),
    );
  });

  it("exports the returned report with the selection that produced it", async () => {
    setup();
    renderReport(
      `/?startDate=2025-01-01&endDate=2025-02-28&granularity=monthly&topN=5&occasionId=${BIRTHDAY}`,
    );
    await userEvent.click(screen.getByRole("button", { name: "Export CSV" }));
    expect(exportCsv).toHaveBeenCalledOnce();
    const request = exportCsv.mock.lastCall![0];
    expect(request.filename).toMatch(/^spending-by-envelope/);
    expect(request.headers).toContain("Series");
    expect(request.rows).toHaveLength(6);
    expect(request.rows[0]).toEqual([
      "envelope",
      "2025-01-01",
      "2025-02-28",
      "monthly",
      5,
      "",
      BIRTHDAY,
      "2025-01",
      "USD",
      GROCERIES,
      "Groceries",
      false,
      false,
      3,
      100,
    ]);
  });

  it("names the export after the occasion grouping", async () => {
    setup({ data: { ...report(), groupBy: "occasion" } });
    renderReport("/?groupBy=occasion");
    await userEvent.click(screen.getByRole("button", { name: "Export CSV" }));
    expect(exportCsv.mock.lastCall![0].filename).toMatch(
      /^spending-by-occasion/,
    );
  });
});
