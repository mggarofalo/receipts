import { describe, it, expect, vi, beforeEach } from "vitest";
import { screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { renderWithProviders } from "@/test/test-utils";
import Ynab from "./Ynab";

vi.mock("@/hooks/usePageTitle", () => ({
  usePageTitle: vi.fn(),
}));

const useYnabStatusMock = vi.fn();
const useYnabSyncEventsMock = vi.fn();

vi.mock("@/hooks/useYnab", async () => {
  // Keep the other hook implementations in case the page ever imports more;
  // we only stub the two it actually uses today.
  return {
    useYnabStatus: () => useYnabStatusMock(),
    useYnabSyncEvents: (...args: unknown[]) => useYnabSyncEventsMock(...args),
  };
});

const STATUS_DEFAULTS = {
  isConfigured: true,
  isConnected: true,
  selectedBudgetId: "budget-abc" as string | null,
  lastSuccessUtc: new Date(Date.now() - 30 * 60_000).toISOString() as string | null,
  lastFailureUtc: null as string | null,
  pushes24h: 5,
  successes24h: 4,
  failures24h: 1,
  pushes7d: 30,
  successes7d: 28,
  failures7d: 2,
  pushes30d: 100,
  successes30d: 95,
  failures30d: 5,
  rateLimit: {
    remainingRequests: 190,
    maxRequests: 200,
    requestsUsed: 10,
    windowResetAt: null,
    oldestRequestAt: null,
  },
};

function mockStatus(overrides: Partial<typeof STATUS_DEFAULTS> = {}) {
  useYnabStatusMock.mockReturnValue({
    status: { ...STATUS_DEFAULTS, ...overrides },
    isLoading: false,
  });
}

function mockEvents(events: object[] = [], totalCount = events.length) {
  useYnabSyncEventsMock.mockReturnValue({
    events,
    totalCount,
    isLoading: false,
  });
}

describe("Ynab status page", () => {
  beforeEach(() => {
    useYnabStatusMock.mockReset();
    useYnabSyncEventsMock.mockReset();
    mockStatus();
    mockEvents();
  });

  it("renders the page heading", () => {
    renderWithProviders(<Ynab />);
    expect(
      screen.getByRole("heading", { name: /ynab status/i, level: 1 }),
    ).toBeInTheDocument();
  });

  it("shows the not-configured alert when YNAB has no PAT", () => {
    mockStatus({ isConfigured: false, isConnected: false, selectedBudgetId: null });
    renderWithProviders(<Ynab />);
    expect(screen.getByText(/yna(b)? isn't configured yet/i)).toBeInTheDocument();
    expect(
      screen.getByRole("link", { name: /connect a personal access token/i }),
    ).toHaveAttribute("href", "/settings/ynab");
  });

  it("renders Connected status when configured + connected", () => {
    renderWithProviders(<Ynab />);
    expect(screen.getByText("Connected")).toBeInTheDocument();
  });

  it("renders Disconnected status when configured but probe failed", () => {
    mockStatus({ isConnected: false });
    renderWithProviders(<Ynab />);
    expect(screen.getByText("Disconnected")).toBeInTheDocument();
  });

  it("renders rolling counts in the activity table", () => {
    renderWithProviders(<Ynab />);
    // The 24h row carries 5/4/1; pick a unique number per row to avoid
    // assertion ambiguity against other "5" / "1" instances on the page.
    expect(screen.getByRole("row", { name: /last 24h.*5.*4.*1/i })).toBeInTheDocument();
    expect(screen.getByRole("row", { name: /last 7 days.*30.*28.*2/i })).toBeInTheDocument();
    expect(screen.getByRole("row", { name: /last 30 days.*100.*95.*5/i })).toBeInTheDocument();
  });

  it("shows EmptyState when no events", () => {
    renderWithProviders(<Ynab />);
    expect(screen.getByText(/no sync events yet/i)).toBeInTheDocument();
  });

  it("renders a row per event with a link to the receipt when present", () => {
    const receiptId = "11111111-2222-3333-4444-555555555555";
    mockEvents(
      [
        {
          id: "ev-1",
          occurredAt: new Date().toISOString(),
          eventType: "transactionPush",
          outcome: "synced",
          receiptId,
          errorMessage: null,
        },
        {
          id: "ev-2",
          occurredAt: new Date(Date.now() - 5 * 60_000).toISOString(),
          eventType: "transactionPush",
          outcome: "failed",
          receiptId: null,
          errorMessage: "401 Unauthorized",
        },
      ],
      2,
    );

    renderWithProviders(<Ynab />);

    // Both outcome pills + filter buttons + status tiles can render the
    // words "Synced"/"Failed". Scope to the recent-activity table so we
    // only count the pills.
    const tables = screen.getAllByRole("table");
    const activityTable = tables[tables.length - 1]!;
    const scoped = within(activityTable);
    expect(scoped.getAllByText(/synced/i).length).toBeGreaterThan(0);
    expect(scoped.getByText(/^failed$/i)).toBeInTheDocument();
    expect(scoped.getByText(/401 Unauthorized/)).toBeInTheDocument();
    expect(
      screen.getByRole("link", { name: /^view$/i }),
    ).toHaveAttribute("href", `/receipts/${receiptId}`);
  });

  it("re-queries with the outcome filter when a filter button is pressed", async () => {
    const user = userEvent.setup();
    renderWithProviders(<Ynab />);

    // Initial call: outcome is undefined (the "all" filter).
    expect(useYnabSyncEventsMock).toHaveBeenLastCalledWith(0, 25, undefined);

    await user.click(screen.getByRole("button", { name: /^failed$/i }));
    expect(useYnabSyncEventsMock).toHaveBeenLastCalledWith(0, 25, "failed");

    await user.click(screen.getByRole("button", { name: /^synced$/i }));
    expect(useYnabSyncEventsMock).toHaveBeenLastCalledWith(0, 25, "synced");
  });
});
