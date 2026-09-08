vi.hoisted(() => vi.stubEnv("VITE_API_URL", "http://ynab-diagnostics.test"));
import { act, cleanup, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { createMemoryRouter, RouterProvider } from "react-router";
import { http, HttpResponse } from "msw";
import { setupServer } from "msw/node";
import { toast } from "sonner";
import { AuthProvider } from "@/contexts/AuthContext";
import { AppearanceProvider } from "@/contexts/AppearanceContext";
import { RootLayout } from "@/components/RootLayout";
import { createAppQueryClient } from "@/lib/query-client";
import { clearTokens, setTokens } from "@/lib/auth";
import { clearServerErrorPageFlag } from "@/lib/server-error-bus";
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

import {
  useYnabConnectionStatus,
  useYnabRateLimitStatus,
} from "@/hooks/useYnab";

const CONFIGURED = {
  isConfigured: true,
  isConnected: true,
  lastSuccessfulSyncUtc: "2026-06-01T00:00:00Z",
  isLoading: false,
};

describe("Ynab", () => {
  beforeEach(() => {
    vi.mocked(useYnabRateLimitStatus).mockReturnValue({
      rateLimitStatus: {
        requestsUsed: 10,
        maxRequests: 200,
        remainingRequests: 190,
        windowResetAt: null,
        oldestRequestAt: null,
      },
    } as ReturnType<typeof useYnabRateLimitStatus>);
  });
  it("renders the health grid and activity table when configured", () => {
    vi.mocked(useYnabConnectionStatus).mockReturnValue(CONFIGURED as never);

    renderWithProviders(<Ynab />);

    expect(
      screen.getByRole("heading", { name: /ynab status/i }),
    ).toBeInTheDocument();
    expect(screen.getByText(/Connected/i)).toBeInTheDocument();
    expect(screen.getByTestId("ynab-events-table")).toBeInTheDocument();
    // Rate-limit progress bar is present and labelled.
    expect(
      screen.getByRole("progressbar", { name: /rate limit/i }),
    ).toBeInTheDocument();
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

describe("Ynab diagnostics rate-limit request ownership", () => {
  // Keep the existing unrelated status/events fixtures; the rate-limit hook,
  // native SDK, session QueryClient, RootLayout and error bridge are real here.
  let actualRateLimitHook: typeof useYnabRateLimitStatus;
  let queryClient: ReturnType<typeof createAppQueryClient>;
  let router: ReturnType<typeof createMemoryRouter>;
  let failed = false;
  let hold = false;
  let requestCount = 0;
  let used = 23;
  let release!: () => void;
  let started!: () => void;
  let heldResponse: Promise<void>;
  let requestStarted: Promise<void>;
  const server = setupServer(
    http.get("*/api/ynab/rate-limit-status", async () => {
      requestCount++;
      if (hold) {
        started();
        await heldResponse;
      }
      return failed
        ? HttpResponse.json(
            { status: 503, detail: "Rate limit unavailable" },
            { status: 503 },
          )
        : HttpResponse.json({
            requestsUsed: used,
            maxRequests: 200,
            remainingRequests: 200 - used,
            windowResetAt: null,
            oldestRequestAt: null,
          });
    }),
  );

  beforeAll(async () => {
    const actual =
      await vi.importActual<typeof import("@/hooks/useYnab")>(
        "@/hooks/useYnab",
      );
    actualRateLimitHook = actual.useYnabRateLimitStatus;
    server.listen({ onUnhandledRequest: "error" });
  });
  afterAll(() => server.close());
  beforeEach(() => {
    failed = false;
    hold = false;
    requestCount = 0;
    used = 23;
    heldResponse = new Promise<void>((resolve) => {
      release = resolve;
    });
    requestStarted = new Promise<void>((resolve) => {
      started = resolve;
    });
    clearServerErrorPageFlag();
    setTokens("alice-access", "alice-refresh");
    queryClient = createAppQueryClient();
    vi.mocked(useYnabConnectionStatus).mockReturnValue(CONFIGURED as never);
    vi.mocked(useYnabRateLimitStatus).mockImplementation(actualRateLimitHook);
  });
  afterEach(async () => {
    cleanup();
    await act(async () => {
      release();
      await heldResponse;
    });
    queryClient.clear();
    clearTokens();
    toast.dismiss();
    vi.restoreAllMocks();
    server.resetHandlers();
  });

  function renderDiagnostics() {
    router = createMemoryRouter(
      [
        {
          element: <RootLayout />,
          children: [
            { path: "/ynab", element: <Ynab /> },
            { path: "/error/500", element: <h1>Global server error route</h1> },
          ],
        },
      ],
      { initialEntries: ["/ynab"] },
    );
    render(
      <AppearanceProvider>
        <AuthProvider queryClientFactory={() => queryClient}>
          <RouterProvider router={router} />
        </AuthProvider>
      </AppearanceProvider>,
    );
  }

  it("keeps an initial rate-limit failure local and retries the actual read", async () => {
    failed = true;
    const errorToast = vi.spyOn(toast, "error");
    renderDiagnostics();
    expect(
      await screen.findByText(
        "YNAB rate-limit status is unavailable. Any usage shown is last known.",
      ),
    ).toBeVisible();
    expect(router.state.location.pathname).toBe("/ynab");
    expect(screen.getByTestId("ynab-events-table")).toBeInTheDocument();
    expect(
      screen.queryByRole("progressbar", { name: /rate limit/i }),
    ).not.toBeInTheDocument();
    expect(errorToast).not.toHaveBeenCalled();
    expect(requestCount).toBe(1);
    failed = false;
    await userEvent
      .setup()
      .click(screen.getByRole("button", { name: "Retry" }));
    expect(await screen.findByText("23 / 200 used")).toBeVisible();
    expect(
      screen.getByRole("progressbar", { name: /rate limit/i }),
    ).toHaveAttribute("aria-valuenow", "23");
    expect(
      screen.queryByRole("button", { name: "Retry" }),
    ).not.toBeInTheDocument();
    expect(requestCount).toBe(2);
    expect(router.state.location.pathname).toBe("/ynab");
  });

  it("retains cached usage through refresh failure and disables the held Retry", async () => {
    const errorToast = vi.spyOn(toast, "error");
    renderDiagnostics();
    expect(await screen.findByText("23 / 200 used")).toBeVisible();
    const progress = screen.getByRole("progressbar", { name: /rate limit/i });
    failed = true;
    await act(async () => {
      await queryClient.invalidateQueries({
        queryKey: ["ynab", "rate-limit-status"],
      });
    });
    expect(await screen.findByRole("button", { name: "Retry" })).toBeEnabled();
    expect(progress).toHaveAttribute("aria-valuenow", "23");
    expect(screen.getByText("177 left")).toBeVisible();
    expect(router.state.location.pathname).toBe("/ynab");
    expect(errorToast).not.toHaveBeenCalled();
    failed = false;
    hold = true;
    used = 24;
    await userEvent
      .setup()
      .click(screen.getByRole("button", { name: "Retry" }));
    await act(async () => {
      await requestStarted;
    });
    expect(screen.getByRole("button", { name: "Retrying…" })).toBeDisabled();
    expect(progress).toBeInTheDocument();
    expect(progress).toHaveAttribute("aria-valuenow", "23");
    expect(screen.getByText("177 left")).toBeVisible();
    expect(requestCount).toBe(3);
    await act(async () => {
      release();
      await heldResponse;
    });
    await waitFor(() =>
      expect(progress).toHaveAttribute("aria-valuenow", "24"),
    );
    expect(screen.getByText("176 left")).toBeVisible();
    expect(
      screen.queryByRole("button", { name: "Retry" }),
    ).not.toBeInTheDocument();
    expect(requestCount).toBe(3);
    expect(errorToast).not.toHaveBeenCalled();
  });
});
