import { act, cleanup, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { QueryClientProvider } from "@tanstack/react-query";
import { http, HttpResponse } from "msw";
import { toast } from "sonner";
import { clearTokens } from "@/lib/auth";
import { createAppQueryClient } from "@/lib/query-client";
import { server } from "@/test/msw/server";
import { renderWithProviders } from "@/test/test-utils";
import { BalanceSummaryCard } from "@/components/BalanceSummaryCard";
import { DateRangeSelector } from "@/components/dashboard/DateRangeSelector";
import { useReceiptItem, useUpdateReceiptItem } from "./useReceiptItems";
import { useCreateReceipt, useReceipts } from "./useReceipts";
import { useTripByReceiptId } from "./useTrips";
import { MergeCardsDialog } from "@/components/MergeCardsDialog";
import { SummaryStats } from "@/components/dashboard/SummaryStats";
import { useOutOfBalanceReport } from "./useOutOfBalanceReport";
import { useYnabSplitComparison } from "./useYnab";
import { usePromoteToTemplate } from "./usePromoteToTemplate";
import { useSimilarItems, useCategoryRecommendations } from "./useSimilarItems";
import { useNormalizedDescriptions } from "./useNormalizedDescriptions";
import { useItemTemplates } from "./useItemTemplates";
import { useSignalR } from "./useSignalR";
import "@/test/setup-combobox-polyfills";

const hub = vi.hoisted(() => ({
  start: vi.fn().mockResolvedValue(undefined),
  stop: vi.fn().mockResolvedValue(undefined),
  on: vi.fn(),
  off: vi.fn(),
  state: "Disconnected",
  onreconnecting: vi.fn(),
  onreconnected: vi.fn(),
  onclose: vi.fn(),
  connectionId: "same-session",
}));
const buffered = vi.hoisted(() => vi.fn());
vi.mock("@/lib/signalr-toast-buffer", () => ({
  bufferToast: buffered,
  clearBufferedToasts: vi.fn(),
}));
vi.mock("@microsoft/signalr", () => ({
  HubConnectionBuilder: class {
    withUrl() {
      return this;
    }
    withAutomaticReconnect() {
      return this;
    }
    configureLogging() {
      return this;
    }
    build() {
      return hub;
    }
  },
  LogLevel: { Debug: 1, None: 6 },
  HubConnectionState: { Disconnected: "Disconnected", Connecting: "Connecting", Connected: "Connected", Reconnecting: "Reconnecting", Disconnecting: "Disconnecting" },
}));

vi.hoisted(() => vi.stubEnv("VITE_API_URL", "http://ledger-projections.test"));
beforeAll(() => server.listen({ onUnhandledRequest: "error" }));
afterAll(() => server.close());
beforeEach(() => {
  vi.clearAllMocks();
  hub.state = "Disconnected";
  hub.start.mockImplementation(async () => { hub.state = "Connected"; });
  hub.stop.mockImplementation(async () => { hub.state = "Disconnected"; });
  localStorage.clear();
  clearTokens();
});
afterEach(() => {
  cleanup();
  server.resetHandlers();
  toast.dismiss();
});

const receipt = {
  id: "receipt",
  location: "Shop",
  date: "2026-01-01",
  taxAmount: 0,
};
const item = {
  id: "item",
  receiptItemCode: "",
  description: "Milk",
  quantity: 1,
  unitPrice: 10,
  category: "Food",
  subcategory: "Dairy",
};
function ItemViews({ remote = false }: { remote?: boolean }) {
  useSignalR(remote);
  const report = useOutOfBalanceReport();
  const split = useYnabSplitComparison("receipt");
  const currentItem = useReceiptItem("item");
  const receipts = useReceipts();
  const trip = useTripByReceiptId("receipt");
  const update = useUpdateReceiptItem();
  return (
    <>
      <SummaryStats dateRange={{}} />
      <output aria-label="Report discrepancy">
        {report.data?.totalDiscrepancy}
      </output>
      <output aria-label="Expected YNAB split">
        {split.data?.transactionComparisons[0]?.expected[0]?.milliunits}
      </output>
      <output aria-label="Item price">{currentItem.data?.unitPrice}</output>
      <output aria-label="Receipt list subtotal">
        {receipts.data?.[0]?.itemSubtotal}
      </output>
      {trip.data && (
        <BalanceSummaryCard
          subtotal={trip.data.receipt.subtotal}
          taxAmount={0}
          adjustmentTotal={0}
          expectedTotal={trip.data.receipt.expectedTotal}
        />
      )}
      <button
        onClick={() => update.mutate({ body: { ...item, unitPrice: 20 } })}
      >
        Change item price
      </button>
    </>
  );
}
function YearView() {
  const create = useCreateReceipt();
  return (
    <>
      <DateRangeSelector value={{}} onChange={() => {}} />
      <button onClick={() => create.mutate({ ...receipt, date: "2020-01-01" })}>
        Create older receipt
      </button>
      <output aria-label="Create state">{create.status}</output>
    </>
  );
}

it.each(["local", "remote", "same-session"] as const)(
  "refreshes rendered ledger, dashboard, report and split views after a %s item change",
  async (origin) => {
    const user = userEvent.setup();
    const queryClient = createAppQueryClient();
    let price = 10;
    let writes = 0;
    queryClient.setQueryData(["receipts", "deleted", 0, 50], { data: [] });
    queryClient.setQueryData(["ynab", "budgets"], { data: [] });
    queryClient.setQueryData(["api-keys", "private-user"], { data: [] });
    server.use(
      http.get("*/api/dashboard/summary", () =>
        HttpResponse.json({
          totalReceipts: 1,
          totalSpent: price,
          averageTripAmount: price,
          mostUsedCategory: { name: "Food", count: 1 },
          mostUsedAccount: { name: "Cash", count: 1 },
        }),
      ),
      http.get("*/api/reports/out-of-balance", () =>
        HttpResponse.json({
          totalCount: 1,
          totalDiscrepancy: price,
          items: [],
        }),
      ),
      http.get("*/api/ynab/receipts/receipt/split-comparison", () =>
        HttpResponse.json({
          canComputeExpected: true,
          unmappedCategories: [],
          transactionComparisons: [
            {
              localTransactionId: "payment",
              accountName: "Cash",
              totalMilliunits: 10000,
              expected: [
                {
                  ynabCategoryId: "food",
                  categoryName: "Food",
                  milliunits: price * 1000,
                },
              ],
            },
          ],
        }),
      ),
      http.get("*/api/receipt-items/item", () =>
        HttpResponse.json({ ...item, unitPrice: price }),
      ),
      http.get("*/api/receipts", () =>
        HttpResponse.json({
          data: [
            {
              ...receipt,
              itemSubtotal: price,
              adjustmentTotal: 0,
              expectedTotal: price,
            },
          ],
          total: 1,
          offset: 0,
          limit: 50,
        }),
      ),
      http.get("*/api/trips", () =>
        HttpResponse.json({
          receipt: {
            receipt,
            items: [],
            adjustments: [],
            subtotal: price,
            adjustmentTotal: 0,
            expectedTotal: price,
            warnings: [],
          },
          transactions: [],
          warnings: [],
        }),
      ),
      http.put("*/api/receipt-items/item", async ({ request }) => {
        const body = (await request.json()) as { unitPrice: number };
        writes++;
        price = body.unitPrice;
        return new HttpResponse(null, { status: 204 });
      }),
    );
    try {
      renderWithProviders(
        <QueryClientProvider client={queryClient}>
          <ItemViews remote={origin !== "local"} />
        </QueryClientProvider>,
      );
      await waitFor(() =>
        expect(
          screen.getByRole("status", { name: "Receipt list subtotal" }),
        ).toHaveTextContent(/^10$/),
      );
      await waitFor(() => {
        expect(screen.getAllByText("$10.00")).toHaveLength(4);
        expect(
          screen.getByRole("status", { name: "Report discrepancy" }),
        ).toHaveTextContent(/^10$/);
        expect(
          screen.getByRole("status", { name: "Expected YNAB split" }),
        ).toHaveTextContent(/^10000$/);
      });
      if (origin === "local") {
        await user.click(
          screen.getByRole("button", { name: "Change item price" }),
        );
      } else {
        await waitFor(() => expect(hub.start).toHaveBeenCalled());
        await waitFor(() => expect(queryClient.isFetching()).toBe(0));
        // Unknown reconnect repairs destination settings; establish a fresh value
        // before checking that this later ordinary item event leaves it alone.
        queryClient.setQueryData(["ynab", "budgets"], { data: [] });
        price = 20;
        const handler = hub.on.mock.calls.find(
          ([event]) => event === "EntityChanged",
        )?.[1] as (notification: object) => void;
        expect(handler).toBeDefined();
        act(() =>
          handler({
            entityType: "receipt-item",
            changeType: "updated",
            id: null,
            connectionId: origin,
          }),
        );
      }
      // The own-item read is a positive control: transport and the ordinary mutation completed.
      await waitFor(() =>
        expect(
          screen.getByRole("status", { name: "Item price" }),
        ).toHaveTextContent(/^20$/),
      );
      expect(price).toBe(20);
      await waitFor(() => {
        expect(
          screen.getByRole("status", { name: "Receipt list subtotal" }),
        ).toHaveTextContent(/^20$/);
        expect(screen.getAllByText("$20.00")).toHaveLength(4);
        expect(
          screen.getByRole("status", { name: "Report discrepancy" }),
        ).toHaveTextContent(/^20$/);
        expect(
          screen.getByRole("status", { name: "Expected YNAB split" }),
        ).toHaveTextContent(/^20000$/);
      });
      expect(writes).toBe(origin === "local" ? 1 : 0);
      expect(buffered).toHaveBeenCalledTimes(origin === "remote" ? 1 : 0);
      expect(
        queryClient.getQueryState(["receipts", "deleted", 0, 50])
          ?.isInvalidated,
      ).toBe(true);
      expect(
        queryClient.getQueryState(["ynab", "budgets"])?.isInvalidated,
      ).toBe(false);
      expect(
        queryClient.getQueryState(["api-keys", "private-user"])?.isInvalidated,
      ).toBe(false);
    } finally {
      cleanup();
      queryClient.clear();
    }
  },
);

it("adds an older created receipt year to the actual date selector without changing its Infinity freshness policy", async () => {
  const user = userEvent.setup();
  const queryClient = createAppQueryClient();
  let earliest = 2026;
  server.use(
    http.get("*/api/dashboard/earliest-receipt-year", () =>
      HttpResponse.json({ year: earliest }),
    ),
    http.post("*/api/receipts", async ({ request }) => {
      const body = (await request.json()) as { date: string };
      earliest = Number(body.date.slice(0, 4));
      return HttpResponse.json(
        { ...receipt, date: body.date },
        { status: 201 },
      );
    }),
  );
  try {
    renderWithProviders(
      <QueryClientProvider client={queryClient}>
        <YearView />
      </QueryClientProvider>,
    );
    await waitFor(() =>
      expect(
        queryClient.getQueryData(["dashboard", "earliest-receipt-year"]),
      ).toEqual({ year: 2026 }),
    );
    screen.getByTestId("year-dropdown").focus();
    await user.keyboard("{ArrowDown}");
    expect(
      within(screen.getByRole("listbox")).getByRole("option", { name: "2026" }),
    ).toBeInTheDocument();
    expect(
      screen.queryByRole("option", { name: "2020" }),
    ).not.toBeInTheDocument();
    await user.keyboard("{Escape}");
    await user.click(
      screen.getByRole("button", { name: "Create older receipt" }),
    );
    await waitFor(() =>
      expect(
        screen.getByRole("status", { name: "Create state" }),
      ).toHaveTextContent("success"),
    );
    expect(earliest).toBe(2020);
    screen.getByTestId("year-dropdown").focus();
    await user.keyboard("{ArrowDown}");
    expect(
      await screen.findByRole("option", { name: "2020" }),
    ).toBeInTheDocument();
  } finally {
    cleanup();
    queryClient.clear();
  }
});

it.each(["card", "account"] as const)(
  "refreshes %s labels in an open real merge dialog after a remote rename",
  async (domain) => {
    const user = userEvent.setup();
    const queryClient = createAppQueryClient();
    const selected = [
      {
        id: "selected",
        name: "Selected",
        cardCode: "1111",
        accountId: "source",
      },
    ];
    let sibling = {
      id: "sibling",
      name: "Old sibling",
      cardCode: "2222",
      accountId: "source",
      isActive: true,
    };
    let accountName = "Old account";
    server.use(
      http.get("*/api/accounts", () =>
        HttpResponse.json({
          data: [{ id: "source", name: accountName, isActive: true }],
          total: 1,
          offset: 0,
          limit: 500,
        }),
      ),
      http.get("*/api/accounts/source/cards", () =>
        HttpResponse.json([...selected, sibling]),
      ),
    );
    function OpenMerge() {
      useSignalR(true);
      return (
        <MergeCardsDialog
          open
          onOpenChange={() => {}}
          selectedCards={selected}
        />
      );
    }
    try {
      renderWithProviders(
        <QueryClientProvider client={queryClient}>
          <OpenMerge />
        </QueryClientProvider>,
      );
      await user.click(
        await screen.findByRole("button", { name: "Include the other 1 card" }),
      );
      expect(screen.getByText("Old sibling")).toBeInTheDocument();
      expect(screen.getByText("2222")).toBeInTheDocument();
      const dialog = screen.getByRole("dialog");
      expect(dialog).toHaveTextContent("Old account");
      sibling = { ...sibling, name: "Renamed sibling", cardCode: "9876" };
      accountName = "Renamed account";
      const handler = hub.on.mock.calls.find(
        ([event]) => event === "EntityChanged",
      )?.[1] as (notification: object) => void;
      act(() =>
        handler({
          entityType: domain,
          changeType: "updated",
          id: null,
          connectionId: "remote",
        }),
      );
      await waitFor(() => {
        expect(dialog).toHaveTextContent("Renamed account");
        expect(screen.getByText("Renamed sibling")).toBeInTheDocument();
        expect(screen.getByText("9876")).toBeInTheDocument();
      });
      expect(screen.queryByText("Old sibling")).not.toBeInTheDocument();
      expect(screen.getByRole("dialog")).toBe(dialog);
    } finally {
      cleanup();
      queryClient.clear();
    }
  },
);

it.each(["promotion", "same-session-created"] as const)(
  "retains current suggestions while %s refreshes templates and canonical registry",
  async (origin) => {
    const user = userEvent.setup();
    const queryClient = createAppQueryClient();
    let created = false;
    let suggestionReads = 0;
    let categoryReads = 0;
    let creates = 0;
    const suggestion = {
      id: "history",
      name: "Milk",
      source: "history",
      similarity: 1,
      defaultCategory: "Food",
    };
    server.use(
      http.get("*/api/item-templates/similar", ({ request }) => {
        const duplicateGuard =
          new URL(request.url).searchParams.get("limit") === "20";
        if (!duplicateGuard) suggestionReads++;
        // A fresh post-create search can temporarily omit the new unembedded template.
        return HttpResponse.json(created ? [] : [suggestion]);
      }),
      http.get("*/api/item-templates/category-suggestions", () => {
        categoryReads++;
        return HttpResponse.json(
          created ? [] : [{ category: "Food", confidence: 0.9 }],
        );
      }),
      http.get("*/api/item-templates", () =>
        HttpResponse.json({
          data: created ? [{ id: "template", name: "Milk" }] : [],
          total: created ? 1 : 0,
          offset: 0,
          limit: 50,
        }),
      ),
      http.get("*/api/normalized-descriptions", () =>
        HttpResponse.json({ items: [], totalCount: created ? 1 : 0 }),
      ),
      http.post("*/api/item-templates", () => {
        creates++;
        created = true;
        return HttpResponse.json(
          { id: "template", name: "Milk" },
          { status: 201 },
        );
      }),
    );
    function Suggestions() {
      useSignalR(origin !== "promotion");
      const similar = useSimilarItems("Milk");
      const recommendations = useCategoryRecommendations("Milk");
      const templates = useItemTemplates();
      const registry = useNormalizedDescriptions();
      const promote = usePromoteToTemplate();
      return (
        <>
          <output aria-label="Visible suggestion">
            {similar.data?.[0]?.name}
          </output>
          <output aria-label="Category recommendation">
            {recommendations.data?.[0]?.category}
          </output>
          <output aria-label="Template count">{templates.total}</output>
          <output aria-label="Canonical count">{registry.total}</output>
          <button onClick={() => promote.mutate({ name: "Milk" })}>
            Promote
          </button>
        </>
      );
    }
    try {
      renderWithProviders(
        <QueryClientProvider client={queryClient}>
          <Suggestions />
        </QueryClientProvider>,
      );
      await waitFor(() => {
        expect(
          screen.getByRole("status", { name: "Visible suggestion" }),
        ).toHaveTextContent("Milk");
        expect(
          screen.getByRole("status", { name: "Category recommendation" }),
        ).toHaveTextContent("Food");
      });
      expect(
        screen.getByRole("status", { name: "Template count" }),
      ).toHaveTextContent(/^0$/);
      expect(
        screen.getByRole("status", { name: "Canonical count" }),
      ).toHaveTextContent(/^0$/);
      const before = [suggestionReads, categoryReads];
      if (origin === "promotion")
        await user.click(screen.getByRole("button", { name: "Promote" }));
      else {
        created = true;
        const handler = hub.on.mock.calls.find(
          ([event]) => event === "EntityChanged",
        )?.[1] as (notification: object) => void;
        act(() =>
          handler({
            entityType: "item-template",
            changeType: "created",
            id: null,
            connectionId: "same-session",
          }),
        );
      }
      await waitFor(() => {
        expect(
          screen.getByRole("status", { name: "Template count" }),
        ).toHaveTextContent(/^1$/);
        expect(
          screen.getByRole("status", { name: "Canonical count" }),
        ).toHaveTextContent(/^1$/);
      });
      expect(
        screen.getByRole("status", { name: "Visible suggestion" }),
      ).toHaveTextContent("Milk");
      expect(
        screen.getByRole("status", { name: "Category recommendation" }),
      ).toHaveTextContent("Food");
      expect([suggestionReads, categoryReads]).toEqual(before);
      expect(creates).toBe(origin === "promotion" ? 1 : 0);
      expect(buffered).not.toHaveBeenCalled();
    } finally {
      cleanup();
      queryClient.clear();
    }
  },
);
