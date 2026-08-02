import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter, Routes, Route } from "react-router";
import { renderWithProviders } from "@/test/test-utils";
import Reports, { REPORTS, DEFAULT_REPORT } from "./Reports";

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

describe("Reports", () => {
  it("renders the page heading", () => {
    renderWithProviders(<Reports />, { route: "/reports" });
    expect(
      screen.getByRole("heading", { name: /reports/i }),
    ).toBeInTheDocument();
  });

  it("renders the report selector dropdown", () => {
    renderWithProviders(<Reports />, { route: "/reports" });
    expect(screen.getByRole("combobox")).toBeInTheDocument();
  });

  it("defaults to the first report when no query param", async () => {
    renderWithProviders(<Reports />, { route: "/reports" });
    expect(
      await screen.findByTestId("report-out-of-balance"),
    ).toBeInTheDocument();
  });

  it("selects the report specified by query param", async () => {
    renderWithProviders(<Reports />, {
      route: "/reports?report=item-cost-over-time",
    });
    expect(
      await screen.findByTestId("report-item-cost-over-time"),
    ).toBeInTheDocument();
  });

  it("falls back to default report for invalid query param", async () => {
    renderWithProviders(<Reports />, {
      route: "/reports?report=nonexistent",
    });
    expect(
      await screen.findByTestId("report-out-of-balance"),
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

  it("calls usePageTitle with the active report name", async () => {
    const { usePageTitle } = await import("@/hooks/usePageTitle");
    renderWithProviders(<Reports />, {
      route: "/reports?report=duplicate-detection",
    });
    expect(usePageTitle).toHaveBeenCalledWith(
      "Reports - Duplicate Detection",
    );
  });

  it("exports REPORTS config with correct number of reports", () => {
    expect(REPORTS).toHaveLength(7);
  });

  it("exports DEFAULT_REPORT as out-of-balance", () => {
    expect(DEFAULT_REPORT).toBe("out-of-balance");
  });

  it("no longer lists Normalized Descriptions among the reports", async () => {
    renderWithProviders(<Reports />, { route: "/reports" });
    const trigger = screen.getByRole("combobox");
    await userEvent.click(trigger);
    expect(
      screen.queryByRole("option", { name: /normalized descriptions/i }),
    ).not.toBeInTheDocument();
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
