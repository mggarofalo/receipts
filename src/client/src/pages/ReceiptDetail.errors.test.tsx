vi.hoisted(() => vi.stubEnv("VITE_API_URL", "http://local-errors.test"));
import {
  act,
  cleanup,
  render,
  screen,
  waitFor,
  within,
} from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter, Route, Routes } from "react-router";
import { http, HttpResponse } from "msw";
import { setupServer } from "msw/node";
import { AuthProvider } from "@/contexts/AuthContext";
import { AppearanceProvider } from "@/contexts/AppearanceContext";
import { TooltipProvider } from "@/components/ui/tooltip";
import { RootLayout } from "@/components/RootLayout";
import { createAppQueryClient } from "@/lib/query-client";
import { clearTokens, setTokens } from "@/lib/auth";
import { clearServerErrorPageFlag } from "@/lib/server-error-bus";
import ReceiptDetail from "./ReceiptDetail";
import "@/test/setup-combobox-polyfills";

const receiptId = "95000000-0000-4000-8000-000000000001";
const trip = {
  receipt: {
    receipt: {
      id: receiptId,
      location: "Draft grocery",
      date: "2026-09-01",
      taxAmount: 0,
    },
    items: [],
    adjustments: [],
    warnings: [],
    subtotal: 0,
    adjustmentTotal: 0,
    expectedTotal: 0,
  },
  transactions: [],
  warnings: [],
};
let tripStatus = 200;
let connectionFails = false;
let tripNetworkFailure = false;
const server = setupServer(
  http.get("*/api/trips", () =>
    tripNetworkFailure
      ? HttpResponse.error()
      : tripStatus === 200
        ? HttpResponse.json(trip)
        : HttpResponse.json(
            { status: tripStatus, detail: "Receipt read unavailable" },
            { status: tripStatus },
          ),
  ),
  http.get("*/api/ynab/connection-status", () =>
    connectionFails
      ? HttpResponse.json(
          { status: 503, detail: "Optional integration unavailable" },
          { status: 503 },
        )
      : HttpResponse.json({ isConfigured: false, isConnected: false }),
  ),
  http.get("*/api/ynab/settings/budget", () =>
    HttpResponse.json({ selectedBudgetId: null }),
  ),
  http.get("*/api/metadata/enums", () =>
    HttpResponse.json({
      adjustmentTypes: [],
      authEventTypes: [],
      auditActions: [],
      entityTypes: [],
    }),
  ),
  http.get("*/api/cards", () =>
    HttpResponse.json({ data: [], total: 0, offset: 0, limit: 500 }),
  ),
  http.get("*/api/audit", () =>
    HttpResponse.json({ data: [], total: 0, offset: 0, limit: 50 }),
  ),
  http.get("*/api/receipts/locations", () => HttpResponse.json({ data: [] })),
);
const clients: ReturnType<typeof createAppQueryClient>[] = [];

function renderReceipt(withBridge = true) {
  const client = createAppQueryClient();
  clients.push(client);
  const page = <Route path="/receipts/:id" element={<ReceiptDetail />} />;
  render(
    <MemoryRouter initialEntries={[`/receipts/${receiptId}`]}>
      <AppearanceProvider>
        <TooltipProvider>
          <AuthProvider queryClientFactory={() => client}>
            <Routes>
              {withBridge ? (
                <Route element={<RootLayout />}>
                  {page}
                  <Route
                    path="/error/500"
                    element={<h1>Global server error route</h1>}
                  />
                </Route>
              ) : (
                page
              )}
            </Routes>
          </AuthProvider>
        </TooltipProvider>
      </AppearanceProvider>
    </MemoryRouter>,
  );
  return client;
}

beforeAll(() => server.listen({ onUnhandledRequest: "error" }));
afterAll(() => server.close());
beforeEach(() => {
  tripStatus = 200;
  connectionFails = false;
  tripNetworkFailure = false;
  clearServerErrorPageFlag();
  setTokens(
    `header.${btoa(JSON.stringify({ sub: "alice", email: "alice@example.test", exp: 4102444800 }))}.signature`,
    "alice-refresh",
  );
});
afterEach(() => {
  cleanup();
  clients.splice(0).forEach((client) => client.clear());
  clearTokens();
  server.resetHandlers();
});

describe("receipt errors keep their local state owner", () => {
  it("keeps the actual edit dialog and unsaved tax when an optional integration refetch fails", async () => {
    const user = userEvent.setup();
    const client = renderReceipt();
    await screen.findByRole("heading", { name: "Draft grocery" });
    await user.click(screen.getByRole("button", { name: /^Edit$/ }));
    const tax = screen.getByLabelText("Tax Amount");
    await user.clear(tax);
    await user.type(tax, "42.25");
    expect(tax).toHaveValue("42.25");
    connectionFails = true;
    await act(async () => {
      await client.invalidateQueries({
        queryKey: ["ynab", "connection-status"],
      });
    });
    expect(screen.getByRole("dialog", { name: "Edit receipt" })).toBeVisible();
    expect(screen.getByLabelText("Tax Amount")).toHaveValue("42.25");
    expect(
      screen.queryByRole("heading", { name: "Global server error route" }),
    ).not.toBeInTheDocument();
    expect(
      await screen.findByText(
        "YNAB is temporarily unavailable. Receipt editing is still available.",
      ),
    ).toBeInTheDocument();
  });

  it("shows not-found only for the actual receipt404 control", async () => {
    tripStatus = 404;
    renderReceipt(false);
    expect(
      await screen.findByRole(
        "heading",
        { name: "Receipt not found" },
        { timeout: 3000 },
      ),
    ).toBeVisible();
  });

  it("treats receipt503 as retryable unavailability rather than a missing receipt", async () => {
    tripStatus = 503;
    // No bridge here: distinguish ReceiptDetail's own false404 from the separately
    // composed draft-loss/navigation regression above.
    const client = renderReceipt(false);
    await waitFor(
      () =>
        expect(client.getQueryState(["trips", receiptId])?.status).toBe(
          "error",
        ),
      { timeout: 3000 },
    );
    expect(
      screen.queryByRole("heading", { name: "Receipt not found" }),
    ).not.toBeInTheDocument();
    const retry = screen.getByRole("button", { name: /retry|try again/i });
    tripStatus = 200;
    await userEvent.click(retry);
    expect(
      await screen.findByRole("heading", { name: "Draft grocery" }),
    ).toBeVisible();
  });
});

it.each(["forbidden", "network"] as const)(
  "keeps a %s receipt failure distinct from not-found and recovers on Retry",
  async (failure) => {
    tripStatus = failure === "forbidden" ? 403 : 200;
    tripNetworkFailure = failure === "network";
    renderReceipt();
    expect(
      await screen.findByRole("heading", { name: "Receipt unavailable" }),
    ).toBeVisible();
    expect(
      screen.queryByRole("heading", { name: "Receipt not found" }),
    ).not.toBeInTheDocument();
    expect(
      screen.queryByRole("heading", { name: "Global server error route" }),
    ).not.toBeInTheDocument();
    if (failure === "forbidden")
      expect(screen.getByText(/do not have permission/i)).toBeVisible();
    tripStatus = 200;
    tripNetworkFailure = false;
    await userEvent.click(screen.getByRole("button", { name: /^Retry$/ }));
    expect(
      await screen.findByRole("heading", { name: "Draft grocery" }),
    ).toBeVisible();
  },
);

it("retains the loaded receipt and edit draft when its own background refresh fails", async () => {
  const user = userEvent.setup();
  const client = renderReceipt();
  await screen.findByRole("heading", { name: "Draft grocery" });
  await user.click(screen.getByRole("button", { name: /^Edit$/ }));
  await user.clear(screen.getByLabelText("Tax Amount"));
  await user.type(screen.getByLabelText("Tax Amount"), "17.42");
  tripStatus = 503;
  await act(async () => {
    await client.invalidateQueries({ queryKey: ["trips", receiptId] });
  });
  await waitFor(() =>
    expect(client.getQueryState(["trips", receiptId])?.status).toBe("error"),
  );
  expect(screen.getByRole("dialog", { name: "Edit receipt" })).toBeVisible();
  expect(screen.getByLabelText("Tax Amount")).toHaveValue("17.42");
  expect(screen.getByText("Could not refresh receipt")).toBeInTheDocument();
  expect(screen.queryByText("Receipt not found")).not.toBeInTheDocument();
  expect(
    screen.queryByText("Global server error route"),
  ).not.toBeInTheDocument();
  tripStatus = 200;
  await act(async () => {
    await client.invalidateQueries({ queryKey: ["trips", receiptId] });
  });
  await waitFor(() =>
    expect(
      screen.queryByText("Could not refresh receipt"),
    ).not.toBeInTheDocument(),
  );
  expect(screen.getByLabelText("Tax Amount")).toHaveValue("17.42");
});

it.each(["connection", "budget"] as const)(
  "retains an open memo-resolution portal but prevents selection during %s unavailability",
  async (failedPrerequisite) => {
    let unavailable = false;
    let resolveCalls = 0;
    server.use(
      http.get("*/api/ynab/connection-status", () =>
        unavailable && failedPrerequisite === "connection"
          ? HttpResponse.json(
              { status: 503, detail: "Connection unavailable" },
              { status: 503 },
            )
          : HttpResponse.json({ isConfigured: true, isConnected: true }),
      ),
      http.get("*/api/ynab/settings/budget", () =>
        unavailable && failedPrerequisite === "budget"
          ? HttpResponse.json(
              { status: 503, detail: "Budget unavailable" },
              { status: 503 },
            )
          : HttpResponse.json({ selectedBudgetId: "budget" }),
      ),
      http.get("*/api/ynab/receipt-sync-statuses", () =>
        HttpResponse.json({ data: [] }),
      ),
      http.get("*/api/ynab/receipts/:id/split-comparison", () =>
        HttpResponse.json({
          canComputeExpected: true,
          transactionComparisons: [],
          unmappedCategories: [],
        }),
      ),
      http.post("*/api/ynab/sync-memos", () =>
        HttpResponse.json({
          results: [
            {
              localTransactionId: "local-1",
              receiptId,
              outcome: "ambiguous",
              ambiguousCandidates: [
                {
                  id: "remote-1",
                  date: "2026-09-01",
                  amount: -10000,
                  payeeName: "Possible grocery",
                  memo: "Existing memo",
                  accountId: "remote-account",
                },
              ],
            },
          ],
        }),
      ),
      http.post("*/api/ynab/sync-memos/resolve", () => {
        resolveCalls++;
        return HttpResponse.json({ success: true });
      }),
    );
    const client = renderReceipt();
    const user = userEvent.setup();
    await user.click(await screen.findByRole("button", { name: "Sync Memos" }));
    await user.click(await screen.findByRole("button", { name: /^Resolve$/ }));
    const dialog = screen.getByRole("dialog", {
      name: "Resolve Ambiguous Match",
    });
    expect(within(dialog).getByText("Possible grocery")).toBeVisible();
    unavailable = true;
    await act(async () => {
      await client.invalidateQueries({
        queryKey:
          failedPrerequisite === "connection"
            ? ["ynab", "connection-status"]
            : ["ynab", "settings", "budget"],
      });
    });
    await waitFor(() =>
      expect(
        screen.getByText(
          "YNAB is temporarily unavailable. Receipt editing is still available.",
        ),
      ).toBeInTheDocument(),
    );
    const select = within(dialog).getByRole("button", { name: "Select" });
    await user.click(select);
    expect(resolveCalls).toBe(0);
    expect(select).toBeDisabled();
    expect(within(dialog).getByText("Possible grocery")).toBeVisible();
    await user.click(within(dialog).getByRole("button", { name: "Close" }));
    expect(
      screen.queryByRole("dialog", { name: "Resolve Ambiguous Match" }),
    ).not.toBeInTheDocument();
    unavailable = false;
    await user.click(screen.getByRole("button", { name: /^Retry$/ }));
    await waitFor(() =>
      expect(screen.getByRole("button", { name: "Sync Memos" })).toBeEnabled(),
    );
    await user.click(screen.getByRole("button", { name: /^Resolve$/ }));
    await user.click(
      within(
        screen.getByRole("dialog", { name: "Resolve Ambiguous Match" }),
      ).getByRole("button", { name: "Select" }),
    );
    await waitFor(() => expect(resolveCalls).toBe(1));
  },
);

function configuredYnabStatuses(
  status: () => number,
  beforeResponse: () => Promise<void> = async () => {},
) {
  server.use(
    http.get("*/api/ynab/connection-status", () =>
      HttpResponse.json({ isConfigured: true, isConnected: true }),
    ),
    http.get("*/api/ynab/settings/budget", () =>
      HttpResponse.json({ selectedBudgetId: "budget" }),
    ),
    http.get("*/api/ynab/receipt-sync-statuses", async () => {
      await beforeResponse();
      return status() === 200
        ? HttpResponse.json({ data: [{ receiptId, syncStatus: "synced" }] })
        : HttpResponse.json(
            { status: status(), detail: "Sync unavailable" },
            { status: status() },
          );
    }),
    http.get("*/api/ynab/receipts/:id/split-comparison", () =>
      HttpResponse.json({
        canComputeExpected: true,
        transactionComparisons: [],
        unmappedCategories: [],
      }),
    ),
  );
}

it("marks a previously synced receipt status as unavailable and last known, then recovers through its own Retry", async () => {
  let status = 200;
  configuredYnabStatuses(() => status);
  const client = renderReceipt();
  expect(await screen.findByLabelText("YNAB: synced")).toBeVisible();
  status = 503;
  await act(async () => {
    await client.invalidateQueries({
      queryKey: ["ynab", "receipt-sync-statuses"],
    });
  });
  const chip = await screen.findByLabelText("YNAB: sync status unavailable");
  expect(chip).toHaveAttribute("title", "Last known: Synced");
  expect(
    screen.getByText(
      "YNAB sync status is unavailable. Any displayed status is last known.",
    ),
  ).toBeVisible();
  expect(screen.queryByLabelText("YNAB: synced")).not.toBeInTheDocument();
  expect(
    screen.queryByRole("heading", { name: "Global server error route" }),
  ).not.toBeInTheDocument();
  status = 200;
  await userEvent.click(screen.getByRole("button", { name: /^Retry$/ }));
  expect(await screen.findByLabelText("YNAB: synced")).toBeVisible();
  expect(
    screen.queryByLabelText("YNAB: sync status unavailable"),
  ).not.toBeInTheDocument();
});

it("shows unknown receipt sync state honestly before any successful response", async () => {
  configuredYnabStatuses(() => 503);
  renderReceipt();
  expect(
    await screen.findByLabelText("YNAB: sync status unavailable"),
  ).toBeVisible();
  expect(
    screen.getByText(
      "YNAB sync status is unavailable. Any displayed status is last known.",
    ),
  ).toBeVisible();
  expect(screen.queryByLabelText("YNAB: synced")).not.toBeInTheDocument();
});

it("distinguishes pending sync lookup from unavailable and synced", async () => {
  let release!: () => void;
  const pending = new Promise<void>((resolve) => {
    release = resolve;
  });
  configuredYnabStatuses(
    () => 200,
    () => pending,
  );
  try {
    renderReceipt();
    expect(
      await screen.findByLabelText("YNAB: checking sync status"),
    ).toBeVisible();
    expect(
      screen.queryByLabelText("YNAB: sync status unavailable"),
    ).not.toBeInTheDocument();
    await act(async () => {
      release();
    });
    expect(await screen.findByLabelText("YNAB: synced")).toBeVisible();
  } finally {
    release();
  }
});

it("keeps deliberate unconfigured integration distinct from a failed prerequisite", async () => {
  let statusReads = 0;
  server.use(
    http.get("*/api/ynab/receipt-sync-statuses", () => {
      statusReads++;
      return HttpResponse.json({ data: [] });
    }),
  );
  const client = renderReceipt();
  await screen.findByRole("heading", { name: "Draft grocery" });
  await waitFor(() =>
    expect(client.getQueryState(["ynab", "connection-status"])?.status).toBe(
      "success",
    ),
  );
  expect(screen.queryByText(/YNAB.*unavailable/i)).not.toBeInTheDocument();
  expect(
    screen.queryByRole("button", { name: "Sync Memos" }),
  ).not.toBeInTheDocument();
  expect(
    screen.queryByLabelText("YNAB: checking sync status"),
  ).not.toBeInTheDocument();
  expect(statusReads).toBe(0);
});

it("does not start an automatic memo resync when prerequisites fail while an authorized resolution is pending", async () => {
  let unavailable = false;
  let syncCalls = 0;
  let resolveCalls = 0;
  let finishResolve!: () => void;
  const pending = new Promise<void>((resolve) => {
    finishResolve = resolve;
  });
  server.use(
    http.get("*/api/ynab/connection-status", () =>
      unavailable
        ? HttpResponse.json(
            { status: 503, detail: "Connection unavailable" },
            { status: 503 },
          )
        : HttpResponse.json({ isConfigured: true, isConnected: true }),
    ),
    http.get("*/api/ynab/settings/budget", () =>
      HttpResponse.json({ selectedBudgetId: "budget" }),
    ),
    http.get("*/api/ynab/receipt-sync-statuses", () =>
      HttpResponse.json({ data: [] }),
    ),
    http.get("*/api/ynab/receipts/:id/split-comparison", () =>
      HttpResponse.json({
        canComputeExpected: true,
        transactionComparisons: [],
        unmappedCategories: [],
      }),
    ),
    http.post("*/api/ynab/sync-memos", () => {
      syncCalls++;
      return HttpResponse.json({
        results: [
          {
            localTransactionId: "local-1",
            receiptId,
            outcome: "ambiguous",
            ambiguousCandidates: [
              {
                id: "remote-1",
                date: "2026-09-01",
                amount: -10000,
                payeeName: "Possible grocery",
                memo: "Existing memo",
                accountId: "remote-account",
              },
            ],
          },
        ],
      });
    }),
    http.post("*/api/ynab/sync-memos/resolve", async () => {
      resolveCalls++;
      await pending;
      return HttpResponse.json({
        localTransactionId: "local-1",
        receiptId,
        outcome: "synced",
        ynabTransactionId: "remote-1",
      });
    }),
  );
  try {
    const client = renderReceipt();
    const user = userEvent.setup();
    await user.click(await screen.findByRole("button", { name: "Sync Memos" }));
    await user.click(await screen.findByRole("button", { name: /^Resolve$/ }));
    await user.click(
      within(
        screen.getByRole("dialog", { name: "Resolve Ambiguous Match" }),
      ).getByRole("button", { name: "Select" }),
    );
    await waitFor(() => expect(resolveCalls).toBe(1));
    unavailable = true;
    await act(async () => {
      await client.invalidateQueries({
        queryKey: ["ynab", "connection-status"],
      });
    });
    await waitFor(() =>
      expect(
        screen.getByText(
          "YNAB is temporarily unavailable. Receipt editing is still available.",
        ),
      ).toBeInTheDocument(),
    );
    await act(async () => {
      finishResolve();
    });
    await waitFor(() =>
      expect(
        screen.queryByRole("dialog", { name: "Resolve Ambiguous Match" }),
      ).not.toBeInTheDocument(),
    );
    expect(syncCalls).toBe(1);
    expect(screen.getByRole("button", { name: "Sync Memos" })).toBeDisabled();
    unavailable = false;
    await user.click(screen.getByRole("button", { name: /^Retry$/ }));
    await waitFor(() =>
      expect(screen.getByRole("button", { name: "Sync Memos" })).toBeEnabled(),
    );
    await user.click(screen.getByRole("button", { name: "Sync Memos" }));
    await waitFor(() => expect(syncCalls).toBe(2));
  } finally {
    finishResolve();
  }
});
