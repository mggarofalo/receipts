import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter, Routes, Route } from "react-router";
import { renderWithProviders } from "@/test/test-utils";
import Reports, { REPORTS, REPORT_GROUPS } from "./Reports";

// jsdom polyfills required by Radix UI Select (used for the report picker).
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
    !(Element.prototype as unknown as { scrollIntoView?: unknown }).scrollIntoView
  ) {
    Element.prototype.scrollIntoView = () => {};
  }
});

vi.mock("@/hooks/usePageTitle", () => ({
  usePageTitle: vi.fn(),
}));

const mockHealthSummary = vi.fn();
vi.mock("@/hooks/useReportsHealthSummary", () => ({
  useReportsHealthSummary: () => mockHealthSummary(),
}));

vi.mock("@/components/reports/OutOfBalance", () => ({
  default: () => <div data-testid="report-out-of-balance">Out of Balance</div>,
}));

vi.mock("@/components/reports/ItemCostOverTime", () => ({
  default: () => (
    <div data-testid="report-item-cost-over-time">Item Cost Over Time</div>
  ),
}));

vi.mock("@/components/reports/SpendingByLocation", () => ({
  default: () => (
    <div data-testid="report-spending-by-location">Spending by Location</div>
  ),
}));

vi.mock("@/components/reports/CategoryTrends", () => ({
  default: () => (
    <div data-testid="report-category-trends">Category Trends</div>
  ),
}));

vi.mock("@/components/reports/DuplicateDetection", () => ({
  default: () => (
    <div data-testid="report-duplicate-detection">Duplicate Detection</div>
  ),
}));

vi.mock("@/components/reports/UncategorizedItems", () => ({
  default: () => (
    <div data-testid="report-uncategorized-items">Uncategorized Items</div>
  ),
}));

vi.mock("@/components/reports/SpendingByNormalizedDescription", () => ({
  default: () => (
    <div data-testid="report-spending-by-normalized-description">
      Spending by Normalized Description
    </div>
  ),
}));

beforeEach(() => {
  vi.clearAllMocks();
  mockHealthSummary.mockReturnValue({
    data: {
      outOfBalanceCount: 3,
      duplicateGroupCount: 1,
      uncategorizedItemCount: 0,
    },
  });
});

describe("Reports", () => {
  it("renders the page heading", () => {
    renderWithProviders(<Reports />, { route: "/reports" });
    expect(
      screen.getByRole("heading", { level: 1, name: /reports/i }),
    ).toBeInTheDocument();
  });

  it("renders the report selector dropdown", () => {
    renderWithProviders(<Reports />, { route: "/reports" });
    expect(screen.getByRole("combobox")).toBeInTheDocument();
  });

  describe("hub landing", () => {
    it("shows the hub instead of a report when no query param is present", () => {
      renderWithProviders(<Reports />, { route: "/reports" });

      expect(screen.queryByTestId("report-out-of-balance")).toBeNull();
      expect(
        screen.getByRole("heading", { level: 2, name: "Spending" }),
      ).toBeInTheDocument();
      expect(
        screen.getByRole("heading", { level: 2, name: "Data Quality" }),
      ).toBeInTheDocument();
    });

    it("renders a card link for every report", () => {
      renderWithProviders(<Reports />, { route: "/reports" });

      for (const report of REPORTS) {
        const link = screen.getByRole("link", {
          name: new RegExp(report.name, "i"),
        });
        expect(link).toHaveAttribute("href", `/reports?report=${report.slug}`);
      }
    });

    it("shows a one-line description on each card", () => {
      renderWithProviders(<Reports />, { route: "/reports" });

      for (const report of REPORTS) {
        expect(screen.getByText(report.description)).toBeInTheDocument();
      }
    });

    it("badges data-quality reports with their live counts", () => {
      renderWithProviders(<Reports />, { route: "/reports" });

      expect(screen.getByText("3 out-of-balance receipts")).toBeInTheDocument();
      expect(screen.getByText("1 duplicate group")).toBeInTheDocument();
    });

    it("shows 'All clear' when a data-quality count is zero", () => {
      renderWithProviders(<Reports />, { route: "/reports" });
      expect(screen.getByText("All clear")).toBeInTheDocument();
    });

    it("omits badges while the health summary is unavailable", () => {
      mockHealthSummary.mockReturnValue({ data: undefined });
      renderWithProviders(<Reports />, { route: "/reports" });

      expect(screen.queryByText("All clear")).toBeNull();
      expect(screen.queryByText(/out-of-balance receipts/)).toBeNull();
    });

    it("opens a report when its card is clicked", async () => {
      renderWithProviders(<Reports />, { route: "/reports" });

      await userEvent.click(
        screen.getByRole("link", { name: /category trends/i }),
      );

      expect(
        await screen.findByTestId("report-category-trends"),
      ).toBeInTheDocument();
    });
  });

  describe("deep links", () => {
    it("selects the report specified by query param", async () => {
      renderWithProviders(<Reports />, {
        route: "/reports?report=item-cost-over-time",
      });
      expect(
        await screen.findByTestId("report-item-cost-over-time"),
      ).toBeInTheDocument();
    });

    it("renders a different report when query param changes", async () => {
      renderWithProviders(<Reports />, {
        route: "/reports?report=category-trends",
      });
      expect(
        await screen.findByTestId("report-category-trends"),
      ).toBeInTheDocument();
    });

    it("falls back to the hub for an invalid query param", () => {
      renderWithProviders(<Reports />, { route: "/reports?report=nonexistent" });

      expect(screen.queryByTestId("report-out-of-balance")).toBeNull();
      expect(
        screen.getByRole("heading", { level: 2, name: "Spending" }),
      ).toBeInTheDocument();
    });

    it("returns to the hub via the All reports button", async () => {
      renderWithProviders(<Reports />, {
        route: "/reports?report=out-of-balance",
      });
      expect(
        await screen.findByTestId("report-out-of-balance"),
      ).toBeInTheDocument();

      await userEvent.click(screen.getByRole("button", { name: /all reports/i }));

      expect(screen.queryByTestId("report-out-of-balance")).toBeNull();
      expect(
        screen.getByRole("heading", { level: 2, name: "Data Quality" }),
      ).toBeInTheDocument();
    });

    it("hides the All reports button on the hub", () => {
      renderWithProviders(<Reports />, { route: "/reports" });
      expect(screen.queryByRole("button", { name: /all reports/i })).toBeNull();
    });
  });

  describe("grouped picker", () => {
    it("renders a section header per group", async () => {
      renderWithProviders(<Reports />, { route: "/reports" });
      await userEvent.click(screen.getByRole("combobox"));

      expect(screen.getByRole("group", { name: "Spending" })).toBeInTheDocument();
      expect(
        screen.getByRole("group", { name: "Data Quality" }),
      ).toBeInTheDocument();
    });

    it("lists every report as an option", async () => {
      renderWithProviders(<Reports />, { route: "/reports" });
      await userEvent.click(screen.getByRole("combobox"));

      for (const report of REPORTS) {
        expect(
          screen.getByRole("option", { name: new RegExp(report.name, "i") }),
        ).toBeInTheDocument();
      }
    });

    it("surfaces non-zero data-quality counts in the picker", async () => {
      renderWithProviders(<Reports />, { route: "/reports" });
      await userEvent.click(screen.getByRole("combobox"));

      const option = screen.getByRole("option", { name: /out of balance/i });
      expect(option).toHaveTextContent("3");
      expect(option).toHaveAccessibleName(/3 out-of-balance receipts/);
    });

    it("omits a picker badge when the count is zero", async () => {
      renderWithProviders(<Reports />, { route: "/reports" });
      await userEvent.click(screen.getByRole("combobox"));

      const option = screen.getByRole("option", {
        name: /uncategorized items/i,
      });
      expect(option).toHaveAccessibleName("Uncategorized Items");
    });

    it("navigates to the chosen report", async () => {
      renderWithProviders(<Reports />, { route: "/reports" });
      await userEvent.click(screen.getByRole("combobox"));
      await userEvent.click(
        screen.getByRole("option", { name: /duplicate detection/i }),
      );

      expect(
        await screen.findByTestId("report-duplicate-detection"),
      ).toBeInTheDocument();
    });

    it("no longer lists Normalized Descriptions among the reports", async () => {
      renderWithProviders(<Reports />, { route: "/reports" });
      await userEvent.click(screen.getByRole("combobox"));

      expect(
        screen.queryByRole("option", { name: /^normalized descriptions/i }),
      ).not.toBeInTheDocument();
    });
  });

  describe("page title", () => {
    it("uses the plain page name on the hub", async () => {
      const { usePageTitle } = await import("@/hooks/usePageTitle");
      renderWithProviders(<Reports />, { route: "/reports" });
      expect(usePageTitle).toHaveBeenCalledWith("Reports");
    });

    it("appends the active report name", async () => {
      const { usePageTitle } = await import("@/hooks/usePageTitle");
      renderWithProviders(<Reports />, {
        route: "/reports?report=duplicate-detection",
      });
      expect(usePageTitle).toHaveBeenCalledWith("Reports - Duplicate Detection");
    });
  });

  describe("config", () => {
    it("exports REPORTS config with correct number of reports", () => {
      expect(REPORTS).toHaveLength(7);
    });

    it("groups reports into Spending and Data Quality", () => {
      expect(REPORT_GROUPS.map((g) => g.label)).toEqual([
        "Spending",
        "Data Quality",
      ]);
      expect(REPORT_GROUPS[0].reports.map((r) => r.slug)).toEqual([
        "spending-by-location",
        "spending-by-normalized-description",
        "category-trends",
        "item-cost-over-time",
      ]);
      expect(REPORT_GROUPS[1].reports.map((r) => r.slug)).toEqual([
        "out-of-balance",
        "duplicate-detection",
        "uncategorized-items",
      ]);
    });

    it("gives every report a one-line description", () => {
      for (const report of REPORTS) {
        expect(report.description.length).toBeGreaterThan(0);
      }
    });

    it("attaches a health metric to every data-quality report only", () => {
      for (const report of REPORT_GROUPS[0].reports) {
        expect(report.metric).toBeUndefined();
      }
      for (const report of REPORT_GROUPS[1].reports) {
        expect(report.metric).toBeDefined();
      }
    });
  });

  it("redirects the legacy normalized-descriptions report link to the admin route", () => {
    // Rendered under real Routes (unlike renderWithProviders' bare
    // MemoryRouter) so the <Navigate> actually swaps the matched route
    // instead of leaving Reports mounted with a now-empty ?report param.
    render(
      <MemoryRouter
        initialEntries={["/reports?report=normalized-descriptions"]}
      >
        <Routes>
          <Route path="/reports" element={<Reports />} />
          <Route
            path="/admin/normalized-descriptions"
            element={<div data-testid="admin-normalized-descriptions" />}
          />
        </Routes>
      </MemoryRouter>,
    );
    expect(
      screen.getByTestId("admin-normalized-descriptions"),
    ).toBeInTheDocument();
    expect(
      screen.queryByRole("heading", { name: /reports/i }),
    ).not.toBeInTheDocument();
  });
});
