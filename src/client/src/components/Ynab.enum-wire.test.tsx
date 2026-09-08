vi.hoisted(() => vi.stubEnv("VITE_API_URL", "http://enum-wire.test"));
import {
  act,
  cleanup,
  fireEvent,
  render,
  screen,
  waitFor,
  within,
} from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { useState, type ReactNode } from "react";
import { createMemoryRouter, RouterProvider } from "react-router";
import { http, HttpResponse } from "msw";
import { setupServer } from "msw/node";
import { toast } from "sonner";
import type { components } from "@/generated/api";
import { AuthProvider } from "@/contexts/AuthContext";
import { AppearanceProvider } from "@/contexts/AppearanceContext";
import { TooltipProvider } from "@/components/ui/tooltip";
import { RootLayout } from "@/components/RootLayout";
import { createAppQueryClient } from "@/lib/query-client";
import { setTokens, clearTokens } from "@/lib/auth";
import { clearServerErrorPageFlag } from "@/lib/server-error-bus";
import { useReceiptYnabSyncStatuses, useYnabSyncStatus } from "@/hooks/useYnab";
import { mockReceiptListItemResponse } from "@/test/mock-api";
import { YnabPushButton } from "./YnabPushButton";
import { YnabMemoSyncCard } from "./YnabMemoSyncCard";
import { YnabBulkSyncCard } from "./YnabBulkSyncCard";
import { ReceiptItemForm } from "./ReceiptItemForm";
import {
  LineItemsSection,
  type ReceiptLineItem,
} from "@/pages/new-receipt/LineItemsSection";
import Receipts from "@/pages/Receipts";
import "@/test/setup-combobox-polyfills";

type WireStatus = components["schemas"]["ReceiptYnabSyncStatusValue"];
type WireOutcome = components["schemas"]["YnabMemoSyncOutcome"];
type WireResult = components["schemas"]["YnabMemoSyncResultItem"];
const receiptId = "88400000-0000-4000-8000-000000000001";
let wireStatus: WireStatus;
let rows: components["schemas"]["ReceiptListItemResponse"][];
let results: WireResult[];
let pushBodies: unknown[];
let resolveBodies: unknown[];
let syncType: string | null;
let statusFailure: boolean;
let memoCalls: number;
let suggestionRequests: { code: string | null; location: string | null }[];
function memoResult(outcome: WireOutcome, index = 0): WireResult {
  return {
    localTransactionId: `88400000-0000-4000-8000-${String(index + 2).padStart(12, "0")}`,
    receiptId,
    outcome,
    ...(outcome === "failed" ? { error: "Remote memo unavailable" } : {}),
    ...(outcome === "ambiguous"
      ? {
          ambiguousCandidates: [
            {
              id: "remote-1",
              date: "2026-09-01",
              amount: -12340,
              payeeName: "Wire payee",
              accountId: "remote-account",
            },
          ],
        }
      : {}),
  };
}
const server = setupServer(
  http.get("*/api/receipt-items/suggestions", ({ request }) => {
    const query = new URL(request.url).searchParams;
    suggestionRequests.push({
      code: query.get("itemCode"),
      location: query.get("location"),
    });
    if (query.get("itemCode") !== "MILK") return HttpResponse.json([]);
    const suggestions: components["schemas"]["ReceiptItemSuggestionResponse"][] =
      [
        {
          itemCode: "MILK",
          description: "Location milk",
          category: "Food",
          unitPrice: 3.459,
          matchType: "location",
        },
        {
          itemCode: "MILK",
          description: "Global milk",
          category: "Food",
          unitPrice: 2.25,
          matchType: "global",
        },
      ];
    return HttpResponse.json(suggestions);
  }),
  http.get("*/api/categories", () =>
    HttpResponse.json({ data: [], total: 0, offset: 0, limit: 500 }),
  ),
  http.get("*/api/item-templates", () =>
    HttpResponse.json({ data: [], total: 0, offset: 0, limit: 500 }),
  ),
  http.get("*/api/item-templates/similar", () => HttpResponse.json([])),
  http.get("*/api/item-templates/category-suggestions", () =>
    HttpResponse.json([]),
  ),
  http.get("*/api/ynab/connection-status", () =>
    HttpResponse.json({ isConfigured: true, isConnected: true }),
  ),
  http.get("*/api/ynab/settings/budget", () =>
    HttpResponse.json({ selectedBudgetId: "budget-1" }),
  ),
  http.get("*/api/ynab/receipt-sync-statuses", () =>
    statusFailure
      ? HttpResponse.json(
          { status: 503, detail: "Status unavailable" },
          { status: 503 },
        )
      : HttpResponse.json({
          data: rows.map((row) => ({
            receiptId: row.id,
            syncStatus: wireStatus,
          })),
        }),
  ),
  http.get("*/api/ynab/sync-status/:id", ({ request }) => {
    syncType = new URL(request.url).searchParams.get("syncType");
    return HttpResponse.json({ status: 404 }, { status: 404 });
  }),
  http.get("*/api/receipts", () =>
    HttpResponse.json({
      data: rows,
      total: rows.length,
      offset: 0,
      limit: 500,
    }),
  ),
  http.post("*/api/ynab/push-transactions", async ({ request }) => {
    pushBodies.push(await request.json());
    return HttpResponse.json({ success: true, pushedTransactions: [] });
  }),
  http.post("*/api/ynab/sync-memos", () => {
    memoCalls++;
    return HttpResponse.json({ results });
  }),
  http.post("*/api/ynab/sync-memos/bulk", () => HttpResponse.json({ results })),
  http.post("*/api/ynab/sync-memos/resolve", async ({ request }) => {
    resolveBodies.push(await request.json());
    results = [memoResult("synced")];
    return HttpResponse.json(results[0]);
  }),
);
const clients: ReturnType<typeof createAppQueryClient>[] = [];
const routers: ReturnType<typeof createMemoryRouter>[] = [];
function renderFeature(feature: ReactNode) {
  const client = createAppQueryClient();
  clients.push(client);
  const router = createMemoryRouter([
    {
      element: <RootLayout />,
      children: [
        { path: "/", element: feature },
        { path: "/error/500", element: <h1>Global error route</h1> },
      ],
    },
  ]);
  routers.push(router);
  render(
    <AppearanceProvider>
      <TooltipProvider>
        <AuthProvider queryClientFactory={() => client}>
          <RouterProvider router={router} />
        </AuthProvider>
      </TooltipProvider>
    </AppearanceProvider>,
  );
  return client;
}
function ReceiptPush() {
  const status = useReceiptYnabSyncStatuses([receiptId]);
  if (status.isLoading) return <p>Checking receipt status</p>;
  return (
    <YnabPushButton
      receiptId={receiptId}
      hasTransactions
      persistedSyncStatus={status.statusMap.get(receiptId)}
      syncStatusUnavailable={status.isError}
    />
  );
}
function TransactionStatus() {
  const status = useYnabSyncStatus(receiptId);
  return (
    <p>
      {status.isSuccess && status.data === null
        ? "No existing sync record"
        : "Reading sync record"}
    </p>
  );
}
beforeAll(() => server.listen({ onUnhandledRequest: "error" }));
afterAll(() => server.close());
beforeEach(() => {
  localStorage.clear();
  setTokens("access", "refresh");
  clearServerErrorPageFlag();
  wireStatus = "notSynced";
  rows = [
    mockReceiptListItemResponse({ id: receiptId, location: "Enum wire store" }),
  ];
  results = [];
  pushBodies = [];
  resolveBodies = [];
  syncType = null;
  statusFailure = false;
  memoCalls = 0;
  suggestionRequests = [];
  vi.spyOn(toast, "success");
  vi.spyOn(toast, "info");
  vi.spyOn(toast, "warning");
});
afterEach(() => {
  cleanup();
  routers.splice(0).forEach((router) => router.dispose());
  clients.splice(0).forEach((client) => client.clear());
  clearTokens();
  server.resetHandlers();
  vi.restoreAllMocks();
});

it.each([
  ["noTransactions", "No transactions"],
  ["balanced", "Balanced"],
  ["outOfBalance", "Out of balance"],
] as const)(
  "renders documented receipt balance %s from HTTP",
  async (balanceState, label) => {
    rows = [
      mockReceiptListItemResponse({
        id: receiptId,
        location: "Enum wire store",
        balanceState,
        paymentSummary: "Card payment",
      }),
    ];
    renderFeature(<Receipts />);
    const row = (await screen.findByText("Enum wire store")).closest("tr")!;
    expect(within(row).getByText(label)).toBeVisible();
  },
);

it.each([
  ["notSynced", null],
  ["pending", "YNAB: pending"],
  ["synced", "YNAB: synced"],
  ["failed", "YNAB: error"],
] as const)(
  "renders documented receipt-list sync state %s from HTTP",
  async (status, label) => {
    wireStatus = status;
    const client = renderFeature(<Receipts />);
    await waitFor(() =>
      expect(
        client.getQueryState(["ynab", "receipt-sync-statuses", [receiptId]])
          ?.status,
      ).toBe("success"),
    );
    const row = screen.getByText("Enum wire store").closest("tr")!;
    if (label) expect(await within(row).findByLabelText(label)).toBeVisible();
    else expect(within(row).queryByLabelText(/^YNAB:/)).not.toBeInTheDocument();
  },
);

it.each([
  ["notSynced", "Not Synced"],
  ["pending", "Pending"],
  ["synced", "Synced"],
  ["failed", "Failed"],
] as const)(
  "uses wire %s for the persisted badge and push eligibility",
  async (status, label) => {
    wireStatus = status;
    renderFeature(<ReceiptPush />);
    expect(
      await screen.findByLabelText(`YNAB sync status: ${label}`),
    ).toBeVisible();
    const button = screen.getByRole("button", {
      name: status === "synced" ? "Already pushed" : "Push to YNAB",
    });
    if (status === "synced") {
      expect(button).toBeDisabled();
      await userEvent.click(button);
      expect(pushBodies).toEqual([]);
    } else {
      expect(button).toBeEnabled();
      await userEvent.click(button);
      await waitFor(() => expect(pushBodies).toEqual([{ receiptId }]));
      expect(
        await screen.findByRole("button", { name: "Pushed to YNAB" }),
      ).toBeDisabled();
    }
  },
);

it("retains unavailable status as a local failure and prohibits a push", async () => {
  statusFailure = true;
  renderFeature(<ReceiptPush />);
  expect(await screen.findByText("Sync status unavailable")).toBeVisible();
  expect(screen.getByRole("button", { name: "Push to YNAB" })).toBeDisabled();
  expect(screen.queryByText("Global error route")).not.toBeInTheDocument();
  expect(pushBodies).toEqual([]);
});

it("uses the documented transactionPush query literal and preserves404 as no record", async () => {
  renderFeature(<TransactionStatus />);
  expect(await screen.findByText("No existing sync record")).toBeVisible();
  expect(syncType).toBe("transactionPush");
});

it("counts actual camel-case single memo outcomes and preserves friendly labels", async () => {
  const outcomes: WireOutcome[] = [
    "synced",
    "synced",
    "alreadySynced",
    "noMatch",
    "ambiguous",
    "currencySkipped",
    "reconciledSkipped",
    "failed",
  ];
  results = outcomes.map(memoResult);
  renderFeature(<YnabMemoSyncCard receiptId={receiptId} />);
  await userEvent.click(
    await screen.findByRole("button", { name: "Sync Memos" }),
  );
  expect(await screen.findByText("2 synced")).toBeVisible();
  for (const label of [
    "1 already synced",
    "1 no match",
    "1 ambiguous",
    "1 reconciled",
    "1 failed",
    "Currency skipped",
  ])
    expect(screen.getByText(label)).toBeVisible();
  expect(toast.success).not.toHaveBeenCalled();
  expect(toast.warning).toHaveBeenCalledWith(
    "Synced 2 transaction memo(s) to YNAB; 1 failed",
  );
  expect(toast.info).not.toHaveBeenCalledWith("No transactions were synced");
});

it("opens a real ambiguous wire result and sends the deliberately selected resolution", async () => {
  results = [memoResult("ambiguous")];
  renderFeature(<YnabMemoSyncCard receiptId={receiptId} />);
  await userEvent.click(
    await screen.findByRole("button", { name: "Sync Memos" }),
  );
  await userEvent.click(await screen.findByRole("button", { name: "Resolve" }));
  const dialog = screen.getByRole("dialog");
  expect(within(dialog).getByText("Wire payee")).toBeVisible();
  await userEvent.click(within(dialog).getByRole("button", { name: "Select" }));
  await waitFor(() =>
    expect(resolveBodies).toEqual([
      {
        localTransactionId: memoResult("ambiguous").localTransactionId,
        ynabTransactionId: "remote-1",
      },
    ]),
  );
  await waitFor(() => expect(memoCalls).toBe(2));
  expect(await screen.findByText("1 synced")).toBeVisible();
});

it("counts camel-case bulk memo wire results without claiming zero successes", async () => {
  results = [
    memoResult("synced"),
    memoResult("synced", 1),
    memoResult("alreadySynced", 2),
  ];
  renderFeature(<YnabBulkSyncCard />);
  const button = await screen.findByRole("button", { name: "Sync All Memos" });
  await waitFor(() => expect(button).toBeEnabled());
  await userEvent.click(button);
  expect(await screen.findByText("2 synced")).toBeVisible();
  expect(screen.getByText("1 already synced")).toBeVisible();
  expect(toast.success).toHaveBeenCalledWith(
    "Synced 2 transaction memo(s) to YNAB",
  );
  expect(toast.success).not.toHaveBeenCalledWith(
    "Synced 0 transaction memo(s) to YNAB",
  );
});

it("keeps a successful empty memo response distinct from enum failures", async () => {
  renderFeature(<YnabMemoSyncCard receiptId={receiptId} />);
  await userEvent.click(
    await screen.findByRole("button", { name: "Sync Memos" }),
  );
  expect(
    await screen.findByText("No transactions found for this receipt."),
  ).toBeVisible();
  expect(resolveBodies).toEqual([]);
  expect(toast.info).toHaveBeenCalledWith("No transactions were synced");
});

function Lines() {
  const [items, setItems] = useState<ReceiptLineItem[]>([]);
  return (
    <LineItemsSection
      items={items}
      onChange={setItems}
      location="Wire market"
    />
  );
}
it.each(["item form", "line entry"] as const)(
  "preserves raw location/global match labels and selected values in %s",
  async (kind) => {
    renderFeature(
      kind === "line entry" ? (
        <Lines />
      ) : (
        <ReceiptItemForm
          mode="edit"
          hideReceiptField
          location="Wire market"
          defaultValues={{
            receiptId,
            description: "",
            receiptItemCode: "",
            quantity: 1,
            unitPrice: 1,
          }}
          onSubmit={() => {}}
          onCancel={() => {}}
        />
      ),
    );
    const user = userEvent.setup();
    const codeInput = screen.getByPlaceholderText(
      kind === "line entry" ? "e.g. MILK-GAL" : "Enter item code...",
    );
    await user.click(codeInput);
    fireEvent.change(codeInput, { target: { value: "MILK" } });
    const locationOption = await screen.findByRole("option", {
      name: /Location milk/,
    });
    const globalOption = screen.getByRole("option", { name: /Global milk/ });
    expect(
      within(locationOption).getByText("Location", { exact: true }),
    ).toBeVisible();
    expect(
      within(globalOption).getByText("Global", { exact: true }),
    ).toBeVisible();
    expect(suggestionRequests).toEqual([
      { code: "MILK", location: "Wire market" },
    ]);
    await user.click(locationOption);
    expect(
      kind === "line entry"
        ? screen.getByPlaceholderText("Item description")
        : screen.getByLabelText(/^Description/),
    ).toHaveValue("Location milk");
    expect(screen.getByLabelText(/^Unit Price/)).toHaveValue("3.4590");
  },
);

it("preserves the receipt-list human last-known status title across a failed read and explicit Retry", async () => {
  wireStatus = "synced";
  const client = renderFeature(<Receipts />);
  expect(await screen.findByLabelText("YNAB: synced")).toBeVisible();
  statusFailure = true;
  await act(async () => {
    await client.invalidateQueries({
      queryKey: ["ynab", "receipt-sync-statuses"],
    });
  });
  const unavailable = await screen.findByLabelText(
    "YNAB: sync status unavailable",
  );
  expect(unavailable).toHaveAttribute("title", "Last known: Synced");
  expect(screen.getByText("Enum wire store")).toBeVisible();
  expect(screen.queryByText("Global error route")).not.toBeInTheDocument();
  statusFailure = false;
  await userEvent.click(
    within(screen.getByRole("alert")).getByRole("button", { name: "Retry" }),
  );
  expect(await screen.findByLabelText("YNAB: synced")).not.toHaveAttribute(
    "title",
  );
  expect(
    screen.queryByLabelText("YNAB: sync status unavailable"),
  ).not.toBeInTheDocument();
});
