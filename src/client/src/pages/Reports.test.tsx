import { screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
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

vi.mock("@/components/reports/NormalizedDescriptions", () => ({
  default: () => (
    <div data-testid="report-normalized-descriptions">
      Normalized Descriptions
    </div>
  ),
}));

vi.mock("@/hooks/usePermission", () => ({
  usePermission: vi.fn(() => ({
    roles: ["User"],
    hasRole: (role: string) => role === "User",
    isAdmin: () => false,
  })),
}));

describe("Reports", () => {
  beforeEach(async () => {
    const { usePermission } = await import("@/hooks/usePermission");
    vi.mocked(usePermission).mockReturnValue({
      roles: ["User"],
      hasRole: (role: string) => role === "User",
      isAdmin: () => false,
    });
  });

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
    expect(REPORTS).toHaveLength(8);
  });

  it("exports DEFAULT_REPORT as out-of-balance", () => {
    expect(DEFAULT_REPORT).toBe("out-of-balance");
  });

  it("hides admin-only reports for non-admin users", async () => {
    renderWithProviders(<Reports />, { route: "/reports" });
    const trigger = screen.getByRole("combobox");
    await userEvent.click(trigger);
    expect(
      screen.queryByRole("option", { name: /normalized descriptions/i }),
    ).not.toBeInTheDocument();
  });

  it("shows admin-only reports for admin users", async () => {
    const { usePermission } = await import("@/hooks/usePermission");
    vi.mocked(usePermission).mockReturnValue({
      roles: ["Admin"],
      hasRole: (role: string) => role === "Admin",
      isAdmin: () => true,
    });
    renderWithProviders(<Reports />, { route: "/reports" });
    const trigger = screen.getByRole("combobox");
    await userEvent.click(trigger);
    expect(
      screen.getByRole("option", { name: /normalized descriptions/i }),
    ).toBeInTheDocument();
  });

  it("falls back to default when admin-only slug is used by non-admin", async () => {
    renderWithProviders(<Reports />, {
      route: "/reports?report=normalized-descriptions",
    });
    expect(
      await screen.findByTestId("report-out-of-balance"),
    ).toBeInTheDocument();
  });
});
