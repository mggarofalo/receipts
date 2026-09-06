import { act, renderHook, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { type ReactNode } from "react";
import { beforeEach, describe, expect, it, vi } from "vitest";

const transport = vi.hoisted(() => ({ GET: vi.fn(), PUT: vi.fn(), POST: vi.fn() }));
const connection = vi.hoisted(() => ({
  start: vi.fn().mockResolvedValue(undefined), stop: vi.fn().mockResolvedValue(undefined),
  on: vi.fn(), onreconnecting: vi.fn(), onreconnected: vi.fn(), onclose: vi.fn(), connectionId: "local-connection",
}));
vi.mock("@/lib/api-client", () => ({ default: transport }));
vi.mock("sonner", () => ({ toast: { success: vi.fn(), error: vi.fn(), info: vi.fn() } }));
vi.mock("@/lib/signalr-toast-buffer", () => ({ bufferToast: vi.fn(), clearBufferedToasts: vi.fn() }));
vi.mock("@microsoft/signalr", () => ({
  HubConnectionBuilder: class {
    withUrl() { return this; }
    withAutomaticReconnect() { return this; }
    configureLogging() { return this; }
    build() { return connection; }
  },
  LogLevel: { Debug: 1, None: 6 },
}));

import { useAllCards, useUpdateCard, useMergeCards } from "./useCards";
import { useTripByReceiptId } from "./useTrips";
import { useTransactions } from "./useTransactions";
import { useDashboardSummary, useDashboardSpendingByAccount } from "./useDashboard";
import { useYnabSplitComparison } from "./useYnab";
import { useReceipts } from "./useReceipts";
import { useSignalR } from "./useSignalR";

beforeEach(() => vi.clearAllMocks());

describe("card ownership cache dependencies", () => {
  it.each(["local", "remote", "merge"] as const)("refreshes mounted ownership views after a %s card update", async (origin) => {
    let owner = "account-before";
    transport.GET.mockImplementation(async (path: string) => {
      const payment = { id: "payment", receiptId: "receipt", cardId: "card", accountId: owner, amount: 10, date: "2025-01-01" };
      const receipt = { id: "receipt", location: "Store", date: "2025-01-01", taxAmount: 0 };
      const account = { id: owner, name: owner, isActive: true };
      switch (path) {
        case "/api/cards": return { data: { data: [{ id: "card", accountId: owner, cardCode: "1234", name: "Card", isActive: true }], total: 1, offset: 0, limit: 500 } };
        case "/api/trips": return { data: { receipt: { receipt, items: [], adjustments: [], subtotal: 10, adjustmentTotal: 0, expectedTotal: 10, warnings: [] }, transactions: [{ transaction: payment, account }], warnings: [] } };
        case "/api/transactions": return { data: { data: [payment], total: 1, offset: 0, limit: 50 } };
        case "/api/dashboard/summary": return { data: { totalReceipts: 1, totalSpent: 10, averageTripAmount: 10, mostUsedAccount: { name: owner, count: 1 }, mostUsedCategory: { name: "Food", count: 1 } } };
        case "/api/dashboard/spending-by-account": return { data: { items: [{ accountId: owner, accountName: owner, amount: 10, percentage: 100 }], totalSpent: 10 } };
        case "/api/ynab/receipts/{receiptId}/split-comparison": return { data: { canComputeExpected: true, unmappedCategories: [], transactionComparisons: [{ localTransactionId: "payment", accountName: owner, totalMilliunits: 10000, expected: [] }] } };
        case "/api/receipts": return { data: { data: owner === "account-before" ? [receipt] : [], total: owner === "account-before" ? 1 : 0, offset: 0, limit: 50 } };
        default: throw new Error(`Unexpected read ${path}`);
      }
    });
    transport.PUT.mockImplementation(async () => { owner = "account-after"; return { error: undefined }; });
    transport.POST.mockImplementation(async () => { owner = "account-after"; return { data: { cardsMoved: 1, accountsRemoved: 1, transactionsRepointed: 1 }, response: { ok: true, status: 200 } }; });
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false, staleTime: Infinity, gcTime: Infinity } } });
    queryClient.setQueryData(["transactions", "deleted", 0, 50], { data: [{ accountId: "account-before" }] });
    queryClient.setQueryData(["categories"], ["Food"]);
    queryClient.setQueryData(["dashboard", "spending-over-time"], { buckets: [] });
    queryClient.setQueryData(["ynab", "connection-status"], { configured: true });
    function Wrapper({ children }: { children: ReactNode }) { return <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>; }
    const { result, unmount } = renderHook(() => {
      const cards = useAllCards();
      const trip = useTripByReceiptId("receipt");
      const transactions = useTransactions();
      const summary = useDashboardSummary({});
      const spending = useDashboardSpendingByAccount({});
      const comparison = useYnabSplitComparison("receipt");
      const receipts = useReceipts(0, 50, null, null, "account-before");
      const update = useUpdateCard();
      const merge = useMergeCards();
      useSignalR(origin === "remote");
      return { cards, trip, transactions, summary, spending, comparison, receipts, update, merge };
    }, { wrapper: Wrapper });
    const visibleOwners = () => [
      result.current.cards.data?.[0]?.accountId,
      result.current.trip.data?.transactions?.[0]?.account.id,
      result.current.transactions.data?.[0]?.accountId,
      result.current.summary.data?.mostUsedAccount.name,
      result.current.spending.data?.items[0]?.accountId,
      result.current.comparison.data?.transactionComparisons[0]?.accountName,
    ];
    await waitFor(() => expect(visibleOwners()).toEqual(Array(6).fill("account-before")));
    expect(result.current.receipts.data).toHaveLength(1);
    if (origin === "local") {
      await act(async () => { await result.current.update.mutateAsync({ id: "card", cardCode: "1234", name: "Card", isActive: true, accountId: "account-after" }); });
      expect(connection.start).not.toHaveBeenCalled();
    } else if (origin === "merge") {
      await act(async () => { await result.current.merge.mutateAsync({ targetAccountId: "account-after", sourceCardIds: ["card"] }); });
      expect(connection.start).not.toHaveBeenCalled();
    } else {
      owner = "account-after";
      const handler = connection.on.mock.calls.find(([event]) => event === "EntityChanged")?.[1] as (notification: object) => void;
      expect(handler).toBeDefined();
      act(() => handler({ entityType: "card", changeType: "updated", id: "card", connectionId: "remote-connection" }));
      expect(transport.PUT).not.toHaveBeenCalled();
    }
    await waitFor(() => expect(visibleOwners()).toEqual(Array(6).fill("account-after")));
    await waitFor(() => expect(result.current.receipts.data).toHaveLength(0));
    expect(queryClient.getQueryState(["transactions", "deleted", 0, 50])?.isInvalidated).toBe(true);
    expect(queryClient.getQueryState(["categories"])?.isInvalidated).toBe(false);
    expect(queryClient.getQueryState(["dashboard", "spending-over-time"])?.isInvalidated).toBe(true);
    expect(queryClient.getQueryState(["ynab", "connection-status"])?.isInvalidated).toBe(false);
    unmount();
    queryClient.clear();
  });
});
