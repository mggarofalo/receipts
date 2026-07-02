import { screen } from "@testing-library/react";
import { renderWithProviders } from "@/test/test-utils";
import "@/test/setup-combobox-polyfills";
import Ynab from "./Ynab";

vi.mock("@/hooks/usePageTitle", () => ({ usePageTitle: vi.fn() }));

vi.mock("@/hooks/useYnab", () => ({
  useYnabConnectionStatus: vi.fn(),
  useYnabRateLimitStatus: vi.fn(() => ({
    rateLimitStatus: {
      requestsUsed: 10,
      maxRequests: 200,
      remainingRequests: 190,
      windowResetAt: null,
      oldestRequestAt: null,
    },
  })),
}));

vi.mock("@/hooks/useYnabStatus", () => ({
  useYnabStatus: vi.fn(() => ({
    data: {
      isConfigured: true,
      lastValidatedAt: "2026-06-01T00:00:00Z",
      lastPushSuccessAt: "2026-06-01T00:00:00Z",
      lastPushFailureAt: null,
      pushCountLast24h: 1,
      pushCountLast7d: 2,
      pushCountLast30d: 3,
      pushSuccessLast30d: 3,
      pushFailureLast30d: 0,
    },
  })),
}));

vi.mock("@/hooks/useYnabEvents", () => ({
  useYnabEvents: vi.fn(() => ({ data: [], total: 0, isLoading: false })),
}));

vi.mock("@/hooks/useServerPagination", () => ({
  useServerPagination: vi.fn(() => ({
    offset: 0,
    limit: 50,
    currentPage: 1,
    pageSize: 50,
    totalPages: () => 1,
    setPage: vi.fn(),
    setPageSize: vi.fn(),
    resetPage: vi.fn(),
  })),
}));

vi.mock("@/hooks/useServerSort", () => ({
  useServerSort: vi.fn(() => ({
    sortBy: "occurredAt",
    sortDirection: "desc",
    toggleSort: vi.fn(),
  })),
}));

vi.mock("@/components/YnabEventsTable", () => ({
  YnabEventsTable: () => <div data-testid="ynab-events-table" />,
}));

vi.mock("@/components/Pagination", () => ({
  Pagination: () => <div data-testid="pagination" />,
}));

import { useYnabConnectionStatus } from "@/hooks/useYnab";

const CONFIGURED = {
  isConfigured: true,
  isConnected: true,
  lastSuccessfulSyncUtc: "2026-06-01T00:00:00Z",
  isLoading: false,
};

describe("Ynab", () => {
  it("renders the health grid and activity table when configured", () => {
    vi.mocked(useYnabConnectionStatus).mockReturnValue(CONFIGURED as never);

    renderWithProviders(<Ynab />);

    expect(screen.getByRole("heading", { name: /ynab status/i })).toBeInTheDocument();
    expect(screen.getByText(/Connected/i)).toBeInTheDocument();
    expect(screen.getByTestId("ynab-events-table")).toBeInTheDocument();
    // Rate-limit progress bar is present and labelled.
    expect(screen.getByRole("progressbar", { name: /rate limit/i })).toBeInTheDocument();
  });

  it("shows the not-configured empty state when no PAT is set", () => {
    vi.mocked(useYnabConnectionStatus).mockReturnValue({
      isConfigured: false,
      isConnected: false,
      lastSuccessfulSyncUtc: null,
      isLoading: false,
    } as never);

    renderWithProviders(<Ynab />);

    expect(screen.getByText(/not configured/i)).toBeInTheDocument();
    expect(screen.queryByTestId("ynab-events-table")).not.toBeInTheDocument();
  });
});
