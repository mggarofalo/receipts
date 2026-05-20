import { screen } from "@testing-library/react";
import { renderWithQueryClient } from "@/test/test-utils";
import { SummaryStats } from "./SummaryStats";

vi.mock("@/hooks/useDashboard", () => ({
  useDashboardSummary: vi.fn(),
}));

import { useDashboardSummary } from "@/hooks/useDashboard";
const mockUseDashboardSummary = vi.mocked(useDashboardSummary);

const dateRange = { startDate: "2024-01-01", endDate: "2024-01-31" };

describe("SummaryStats", () => {
  it("renders loading state", () => {
    mockUseDashboardSummary.mockReturnValue({
      data: undefined,
      isLoading: true,
    } as ReturnType<typeof useDashboardSummary>);

    renderWithQueryClient(<SummaryStats dateRange={dateRange} />);
    expect(screen.getByText("Total receipts")).toBeInTheDocument();
    expect(screen.getByText("Total spent")).toBeInTheDocument();
    expect(screen.getByText("Avg trip")).toBeInTheDocument();
    expect(screen.getByText("Top category")).toBeInTheDocument();
  });

  it("renders data when loaded", () => {
    mockUseDashboardSummary.mockReturnValue({
      data: {
        totalReceipts: 42,
        totalSpent: 1234.56,
        averageTripAmount: 29.39,
        mostUsedAccount: { name: "Visa", count: 20 },
        mostUsedCategory: { name: "Food", count: 15 },
      },
      isLoading: false,
    } as ReturnType<typeof useDashboardSummary>);

    renderWithQueryClient(<SummaryStats dateRange={dateRange} />);
    expect(screen.getByText("42")).toBeInTheDocument();
    expect(screen.getByText("$1,234.56")).toBeInTheDocument();
    expect(screen.getByText("$29.39")).toBeInTheDocument();
    expect(screen.getByText("Food")).toBeInTheDocument();
    expect(screen.getByText("15 receipts")).toBeInTheDocument();
  });

  it("renders dash when no category data", () => {
    mockUseDashboardSummary.mockReturnValue({
      data: {
        totalReceipts: 0,
        totalSpent: 0,
        averageTripAmount: 0,
        mostUsedAccount: { name: null, count: 0 },
        mostUsedCategory: { name: null, count: 0 },
      },
      isLoading: false,
    } as ReturnType<typeof useDashboardSummary>);

    renderWithQueryClient(<SummaryStats dateRange={dateRange} />);
    expect(screen.getByText("—")).toBeInTheDocument();
  });
});
