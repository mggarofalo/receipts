import { screen } from "@testing-library/react";
import { renderWithQueryClient } from "@/test/test-utils";
import Dashboard from "./Dashboard";

vi.mock("@/hooks/usePageTitle", () => ({
  usePageTitle: vi.fn(),
}));

vi.mock("@/hooks/useReceipts", () => ({
  useReceipts: vi.fn(() => ({
    data: [],
    total: 0,
    isLoading: false,
  })),
}));

vi.mock("@/components/dashboard/DateRangeSelector", () => ({
  DateRangeSelector: () => <div data-testid="date-range-selector" />,
}));

vi.mock("@/components/dashboard/SummaryStats", () => ({
  SummaryStats: () => <div data-testid="summary-stats" />,
}));

vi.mock("@/components/dashboard/SpendingOverTimeWidget", () => ({
  SpendingOverTimeWidget: () => <div data-testid="spending-over-time" />,
}));

vi.mock("@/components/dashboard/SpendingByCategoryWidget", () => ({
  SpendingByCategoryWidget: () => <div data-testid="spending-by-category" />,
}));

vi.mock("@/components/dashboard/SpendingByAccountWidget", () => ({
  SpendingByAccountWidget: () => <div data-testid="spending-by-account" />,
}));

vi.mock("@/components/dashboard/SpendingByStoreWidget", () => ({
  SpendingByStoreWidget: () => <div data-testid="spending-by-store" />,
}));

describe("Dashboard", () => {
  it("renders the page heading", () => {
    renderWithQueryClient(<Dashboard />);
    expect(
      screen.getByRole("heading", { name: /dashboard/i }),
    ).toBeInTheDocument();
  });

  it("renders the date range selector", () => {
    renderWithQueryClient(<Dashboard />);
    expect(screen.getByTestId("date-range-selector")).toBeInTheDocument();
  });

  it("renders all widget sections", () => {
    renderWithQueryClient(<Dashboard />);
    expect(screen.getByTestId("summary-stats")).toBeInTheDocument();
    expect(screen.getByTestId("spending-over-time")).toBeInTheDocument();
    expect(screen.getByTestId("spending-by-category")).toBeInTheDocument();
    expect(screen.getByTestId("spending-by-account")).toBeInTheDocument();
    expect(screen.getByTestId("spending-by-store")).toBeInTheDocument();
  });

  it("calls usePageTitle with Dashboard", async () => {
    const { usePageTitle } = await import("@/hooks/usePageTitle");
    renderWithQueryClient(<Dashboard />);
    expect(usePageTitle).toHaveBeenCalledWith("Dashboard");
  });

  // RECEIPTS-742: dashboard recent strip used to render only "Loading…"
  // text. Replace with a proper skeleton + aria-live announcement.
  it("renders a skeleton placeholder for the recent strip while loading", async () => {
    const { useReceipts } = await import("@/hooks/useReceipts");
    vi.mocked(useReceipts).mockReturnValue({
      data: undefined,
      total: 0,
      isLoading: true,
    } as unknown as ReturnType<typeof useReceipts>);

    renderWithQueryClient(<Dashboard />);
    // The recent-strip becomes a live region while loading and contains
    // the SR-only "Loading recent receipts…" message.
    expect(
      screen.getByText(/loading recent receipts/i),
    ).toBeInTheDocument();
  });

  it("shows an empty state with a New receipt CTA when there are no receipts", async () => {
    const { useReceipts } = await import("@/hooks/useReceipts");
    vi.mocked(useReceipts).mockReturnValue({
      data: [],
      total: 0,
      isLoading: false,
    } as unknown as ReturnType<typeof useReceipts>);

    renderWithQueryClient(<Dashboard />);
    expect(screen.getByText("No receipts yet")).toBeInTheDocument();
    const cta = screen.getAllByRole("link", { name: /new receipt/i });
    // One in the PageHead actions, one in the empty-state card.
    expect(cta.length).toBeGreaterThanOrEqual(2);
  });
});
