import { describe, it, expect, vi, beforeEach, type Mock } from "vitest";
import { renderHook, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { createElement, type ReactNode } from "react";

vi.mock("@/lib/api-client", () => ({
  default: {
    GET: vi.fn(),
    POST: vi.fn(),
    PUT: vi.fn(),
    DELETE: vi.fn(),
  },
}));

vi.mock("sonner", () => ({
  toast: { success: vi.fn(), error: vi.fn(), info: vi.fn() },
}));

import client from "@/lib/api-client";
import { toast } from "sonner";
import {
  useYnabBudgets,
  useSelectedYnabBudget,
  useSelectYnabBudget,
  useYnabAccounts,
  useYnabAccountMappings,
  useCreateYnabAccountMapping,
  useUpdateYnabAccountMapping,
  useDeleteYnabAccountMapping,
  useYnabCategories,
  useDistinctReceiptItemCategories,
  useYnabCategoryMappings,
  useUnmappedCategories,
  useCreateYnabCategoryMapping,
  useUpdateYnabCategoryMapping,
  useDeleteYnabCategoryMapping,
  useSyncYnabMemos,
  useSyncYnabMemosBulk,
  useResolveYnabMemoSync,
  useMemoSyncSummary,
  usePushYnabTransactions,
  useBulkPushYnabTransactions,
} from "./useYnab";

function createWrapper() {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false, gcTime: 0 } },
  });
  return function Wrapper({ children }: { children: ReactNode }) {
    return createElement(QueryClientProvider, { client: queryClient }, children);
  };
}

beforeEach(() => {
  vi.clearAllMocks();
});

describe("useYnab", () => {
  it("useYnabBudgets returns budgets on success", async () => {
    const budgets = [
      { id: "budget-1", name: "My Budget" },
      { id: "budget-2", name: "Other Budget" },
    ];
    (client.GET as Mock).mockResolvedValue({ data: { data: budgets }, error: undefined });

    const { result } = renderHook(() => useYnabBudgets(), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(result.current.budgets).toEqual(budgets);
    expect(client.GET).toHaveBeenCalledWith("/api/ynab/budgets");
  });

  it("useYnabBudgets returns empty array when data is undefined", async () => {
    (client.GET as Mock).mockResolvedValue({ data: undefined, error: "Service unavailable" });

    const { result } = renderHook(() => useYnabBudgets(), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isError).toBe(true));
    expect(result.current.budgets).toEqual([]);
  });

  it("useSelectedYnabBudget returns selected budget id", async () => {
    const budgetId = "budget-123";
    (client.GET as Mock).mockResolvedValue({ data: { selectedBudgetId: budgetId }, error: undefined });

    const { result } = renderHook(() => useSelectedYnabBudget(), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(result.current.selectedBudgetId).toBe(budgetId);
  });

  it("useSelectedYnabBudget returns null when no budget selected", async () => {
    (client.GET as Mock).mockResolvedValue({ data: { selectedBudgetId: null }, error: undefined });

    const { result } = renderHook(() => useSelectedYnabBudget(), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(result.current.selectedBudgetId).toBeNull();
  });

  it("useSelectYnabBudget calls PUT and shows toast on success", async () => {
    (client.PUT as Mock).mockResolvedValue({ error: undefined });

    const { result } = renderHook(() => useSelectYnabBudget(), {
      wrapper: createWrapper(),
    });

    await result.current.mutateAsync("budget-123");

    expect(client.PUT).toHaveBeenCalledWith("/api/ynab/settings/budget", {
      body: { budgetId: "budget-123" },
    });
    expect(toast.success).toHaveBeenCalledWith("YNAB budget selected");
  });

  it("useSelectYnabBudget shows error toast on failure", async () => {
    (client.PUT as Mock).mockResolvedValue({ error: "Failed" });

    const { result } = renderHook(() => useSelectYnabBudget(), {
      wrapper: createWrapper(),
    });

    await expect(result.current.mutateAsync("budget-123")).rejects.toThrow();

    await waitFor(() => {
      expect(toast.error).toHaveBeenCalledWith("Failed to select YNAB budget");
    });
  });

  it("useYnabAccounts returns accounts on success", async () => {
    const accounts = [
      { id: "acc-1", name: "Checking", type: "checking", onBudget: true, closed: false, balance: 100000 },
      { id: "acc-2", name: "Savings", type: "savings", onBudget: true, closed: false, balance: 50000 },
    ];
    (client.GET as Mock).mockResolvedValue({ data: { data: accounts }, error: undefined });

    const { result } = renderHook(() => useYnabAccounts(), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(result.current.accounts).toEqual(accounts);
    expect(client.GET).toHaveBeenCalledWith("/api/ynab/accounts");
  });

  it("useYnabAccounts returns empty array on error", async () => {
    (client.GET as Mock).mockResolvedValue({ data: undefined, error: "Service unavailable" });

    const { result } = renderHook(() => useYnabAccounts(), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isError).toBe(true));
    expect(result.current.accounts).toEqual([]);
  });

  it("useYnabAccountMappings returns mappings on success", async () => {
    const mappings = [
      { id: "m1", receiptsAccountId: "a1", ynabAccountId: "y1", ynabAccountName: "Checking", ynabBudgetId: "b1" },
    ];
    (client.GET as Mock).mockResolvedValue({ data: { data: mappings }, error: undefined });

    const { result } = renderHook(() => useYnabAccountMappings(), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(result.current.mappings).toEqual(mappings);
    expect(client.GET).toHaveBeenCalledWith("/api/ynab/account-mappings");
  });

  it("useCreateYnabAccountMapping calls POST and shows toast", async () => {
    (client.POST as Mock).mockResolvedValue({ data: { id: "new-id" }, error: undefined });

    const { result } = renderHook(() => useCreateYnabAccountMapping(), {
      wrapper: createWrapper(),
    });

    await result.current.mutateAsync({
      receiptsAccountId: "a1",
      ynabAccountId: "y1",
      ynabAccountName: "Checking",
      ynabBudgetId: "b1",
    });

    expect(client.POST).toHaveBeenCalledWith("/api/ynab/account-mappings", {
      body: {
        receiptsAccountId: "a1",
        ynabAccountId: "y1",
        ynabAccountName: "Checking",
        ynabBudgetId: "b1",
      },
    });
    expect(toast.success).toHaveBeenCalledWith("Account mapping created");
  });

  it("useUpdateYnabAccountMapping calls PUT and shows toast", async () => {
    (client.PUT as Mock).mockResolvedValue({ error: undefined });

    const { result } = renderHook(() => useUpdateYnabAccountMapping(), {
      wrapper: createWrapper(),
    });

    await result.current.mutateAsync({
      id: "m1",
      ynabAccountId: "y2",
      ynabAccountName: "Savings",
      ynabBudgetId: "b1",
    });

    expect(client.PUT).toHaveBeenCalledWith("/api/ynab/account-mappings/{id}", {
      params: { path: { id: "m1" } },
      body: {
        ynabAccountId: "y2",
        ynabAccountName: "Savings",
        ynabBudgetId: "b1",
      },
    });
    expect(toast.success).toHaveBeenCalledWith("Account mapping updated");
  });

  it("useDeleteYnabAccountMapping calls DELETE and shows toast", async () => {
    (client.DELETE as Mock).mockResolvedValue({ error: undefined });

    const { result } = renderHook(() => useDeleteYnabAccountMapping(), {
      wrapper: createWrapper(),
    });

    await result.current.mutateAsync("m1");

    expect(client.DELETE).toHaveBeenCalledWith("/api/ynab/account-mappings/{id}", {
      params: { path: { id: "m1" } },
    });
    expect(toast.success).toHaveBeenCalledWith("Account mapping removed");
  });

  it("useCreateYnabAccountMapping shows error toast on failure", async () => {
    (client.POST as Mock).mockResolvedValue({ error: "Failed" });

    const { result } = renderHook(() => useCreateYnabAccountMapping(), {
      wrapper: createWrapper(),
    });

    await expect(
      result.current.mutateAsync({
        receiptsAccountId: "a1",
        ynabAccountId: "y1",
        ynabAccountName: "Checking",
        ynabBudgetId: "b1",
      }),
    ).rejects.toThrow();

    await waitFor(() => {
      expect(toast.error).toHaveBeenCalledWith("Failed to create account mapping");
    });
  });

  it("useYnabCategories returns categories on success", async () => {
    const categories = [
      { id: "cat-1", name: "Groceries", categoryGroupId: "group-1", categoryGroupName: "Needs", hidden: false },
      { id: "cat-2", name: "Rent", categoryGroupId: "group-1", categoryGroupName: "Needs", hidden: false },
    ];
    (client.GET as Mock).mockResolvedValue({ data: { data: categories }, error: undefined });

    const { result } = renderHook(() => useYnabCategories(), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(result.current.categories).toEqual(categories);
    expect(client.GET).toHaveBeenCalledWith("/api/ynab/categories");
  });

  it("useYnabCategories returns empty array on error", async () => {
    (client.GET as Mock).mockResolvedValue({ data: undefined, error: "Service unavailable" });

    const { result } = renderHook(() => useYnabCategories(), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isError).toBe(true));
    expect(result.current.categories).toEqual([]);
  });

  it("useDistinctReceiptItemCategories returns categories on success", async () => {
    const categories = ["Electronics", "Groceries", "Pharmacy"];
    (client.GET as Mock).mockResolvedValue({ data: { categories }, error: undefined });

    const { result } = renderHook(() => useDistinctReceiptItemCategories(), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(result.current.categories).toEqual(categories);
    expect(client.GET).toHaveBeenCalledWith("/api/receipt-items/distinct-categories");
  });

  it("useYnabCategoryMappings returns mappings on success", async () => {
    const mappings = [
      { id: "m-1", receiptsCategory: "Groceries", ynabCategoryId: "cat-1", ynabCategoryName: "Groceries", ynabCategoryGroupName: "Needs", ynabBudgetId: "budget-1", createdAt: "2024-01-01T00:00:00Z", updatedAt: "2024-01-01T00:00:00Z" },
    ];
    (client.GET as Mock).mockResolvedValue({ data: { data: mappings }, error: undefined });

    const { result } = renderHook(() => useYnabCategoryMappings(), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(result.current.mappings).toEqual(mappings);
    expect(client.GET).toHaveBeenCalledWith("/api/ynab/category-mappings");
  });

  it("useUnmappedCategories returns unmapped list on success", async () => {
    const unmappedCategories = ["Electronics", "Pharmacy"];
    (client.GET as Mock).mockResolvedValue({ data: { unmappedCategories }, error: undefined });

    const { result } = renderHook(() => useUnmappedCategories(), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(result.current.unmappedCategories).toEqual(unmappedCategories);
    expect(client.GET).toHaveBeenCalledWith("/api/ynab/category-mappings/unmapped");
  });

  it("useCreateYnabCategoryMapping calls POST and shows toast on success", async () => {
    (client.POST as Mock).mockResolvedValue({
      data: { id: "m-1", receiptsCategory: "Groceries" },
      error: undefined,
    });

    const { result } = renderHook(() => useCreateYnabCategoryMapping(), {
      wrapper: createWrapper(),
    });

    await result.current.mutateAsync({
      receiptsCategory: "Groceries",
      ynabCategoryId: "cat-1",
      ynabCategoryName: "Groceries",
      ynabCategoryGroupName: "Needs",
      ynabBudgetId: "budget-1",
    });

    expect(client.POST).toHaveBeenCalledWith("/api/ynab/category-mappings", {
      body: {
        receiptsCategory: "Groceries",
        ynabCategoryId: "cat-1",
        ynabCategoryName: "Groceries",
        ynabCategoryGroupName: "Needs",
        ynabBudgetId: "budget-1",
      },
    });
    expect(toast.success).toHaveBeenCalledWith("Category mapping created");
  });

  it("useUpdateYnabCategoryMapping calls PUT and shows toast on success", async () => {
    (client.PUT as Mock).mockResolvedValue({ error: undefined });

    const { result } = renderHook(() => useUpdateYnabCategoryMapping(), {
      wrapper: createWrapper(),
    });

    await result.current.mutateAsync({
      id: "m-1",
      ynabCategoryId: "cat-2",
      ynabCategoryName: "Rent",
      ynabCategoryGroupName: "Needs",
      ynabBudgetId: "budget-1",
    });

    expect(client.PUT).toHaveBeenCalledWith("/api/ynab/category-mappings/{id}", {
      params: { path: { id: "m-1" } },
      body: {
        ynabCategoryId: "cat-2",
        ynabCategoryName: "Rent",
        ynabCategoryGroupName: "Needs",
        ynabBudgetId: "budget-1",
      },
    });
    expect(toast.success).toHaveBeenCalledWith("Category mapping updated");
  });

  it("useDeleteYnabCategoryMapping calls DELETE and shows toast on success", async () => {
    (client.DELETE as Mock).mockResolvedValue({ error: undefined });

    const { result } = renderHook(() => useDeleteYnabCategoryMapping(), {
      wrapper: createWrapper(),
    });

    await result.current.mutateAsync("m-1");

    expect(client.DELETE).toHaveBeenCalledWith("/api/ynab/category-mappings/{id}", {
      params: { path: { id: "m-1" } },
    });
    expect(toast.success).toHaveBeenCalledWith("Category mapping deleted");
  });

  it("useCreateYnabCategoryMapping shows error toast on failure", async () => {
    (client.POST as Mock).mockResolvedValue({ error: "Conflict" });

    const { result } = renderHook(() => useCreateYnabCategoryMapping(), {
      wrapper: createWrapper(),
    });

    await expect(
      result.current.mutateAsync({
        receiptsCategory: "Groceries",
        ynabCategoryId: "cat-1",
        ynabCategoryName: "Groceries",
        ynabCategoryGroupName: "Needs",
        ynabBudgetId: "budget-1",
      }),
    ).rejects.toThrow();

    await waitFor(() => {
      expect(toast.error).toHaveBeenCalledWith("Failed to create category mapping");
    });
  });

  it("useSyncYnabMemos calls POST and shows toast on success", async () => {
    const syncResults = {
      results: [
        { localTransactionId: "tx-1", receiptId: "r-1", outcome: "Synced", ynabTransactionId: "yt-1" },
      ],
    };
    (client.POST as Mock).mockResolvedValue({ data: syncResults, error: undefined });

    const { result } = renderHook(() => useSyncYnabMemos(), {
      wrapper: createWrapper(),
    });

    await result.current.mutateAsync("r-1");

    expect(client.POST).toHaveBeenCalledWith("/api/ynab/sync-memos", {
      body: { receiptId: "r-1" },
    });
    expect(toast.success).toHaveBeenCalledWith("Synced 1 transaction memo(s) to YNAB");
  });

  it("useSyncYnabMemos shows info toast when no transactions synced", async () => {
    const syncResults = {
      results: [
        { localTransactionId: "tx-1", receiptId: "r-1", outcome: "NoMatch" },
      ],
    };
    (client.POST as Mock).mockResolvedValue({ data: syncResults, error: undefined });

    const { result } = renderHook(() => useSyncYnabMemos(), {
      wrapper: createWrapper(),
    });

    await result.current.mutateAsync("r-1");

    expect(toast.info).toHaveBeenCalledWith("No transactions were synced");
  });

  it("useSyncYnabMemos shows error toast on failure", async () => {
    (client.POST as Mock).mockResolvedValue({ error: "Server error" });

    const { result } = renderHook(() => useSyncYnabMemos(), {
      wrapper: createWrapper(),
    });

    await expect(result.current.mutateAsync("r-1")).rejects.toThrow();

    await waitFor(() => {
      expect(toast.error).toHaveBeenCalledWith("Failed to sync YNAB memos");
    });
  });

  it("useSyncYnabMemosBulk calls POST and shows toast on success", async () => {
    const syncResults = {
      results: [
        { localTransactionId: "tx-1", receiptId: "r-1", outcome: "Synced", ynabTransactionId: "yt-1" },
        { localTransactionId: "tx-2", receiptId: "r-2", outcome: "Synced", ynabTransactionId: "yt-2" },
      ],
    };
    (client.POST as Mock).mockResolvedValue({ data: syncResults, error: undefined });

    const { result } = renderHook(() => useSyncYnabMemosBulk(), {
      wrapper: createWrapper(),
    });

    await result.current.mutateAsync(["r-1", "r-2"]);

    expect(client.POST).toHaveBeenCalledWith("/api/ynab/sync-memos/bulk", {
      body: { receiptIds: ["r-1", "r-2"] },
    });
    expect(toast.success).toHaveBeenCalledWith("Synced 2 transaction memo(s) to YNAB");
  });

  it("useSyncYnabMemosBulk shows error toast on failure", async () => {
    (client.POST as Mock).mockResolvedValue({ error: "Server error" });

    const { result } = renderHook(() => useSyncYnabMemosBulk(), {
      wrapper: createWrapper(),
    });

    await expect(result.current.mutateAsync(["r-1"])).rejects.toThrow();

    await waitFor(() => {
      expect(toast.error).toHaveBeenCalledWith("Failed to bulk sync YNAB memos");
    });
  });

  it("useResolveYnabMemoSync calls POST and shows toast on success", async () => {
    const resolved = {
      localTransactionId: "tx-1",
      receiptId: "r-1",
      outcome: "Synced",
      ynabTransactionId: "yt-1",
    };
    (client.POST as Mock).mockResolvedValue({ data: resolved, error: undefined });

    const { result } = renderHook(() => useResolveYnabMemoSync(), {
      wrapper: createWrapper(),
    });

    await result.current.mutateAsync({
      localTransactionId: "tx-1",
      ynabTransactionId: "yt-1",
    });

    expect(client.POST).toHaveBeenCalledWith("/api/ynab/sync-memos/resolve", {
      body: { localTransactionId: "tx-1", ynabTransactionId: "yt-1" },
    });
    expect(toast.success).toHaveBeenCalledWith("YNAB memo sync resolved");
  });

  it("useResolveYnabMemoSync shows error toast on failure", async () => {
    (client.POST as Mock).mockResolvedValue({ error: "Server error" });

    const { result } = renderHook(() => useResolveYnabMemoSync(), {
      wrapper: createWrapper(),
    });

    await expect(
      result.current.mutateAsync({
        localTransactionId: "tx-1",
        ynabTransactionId: "yt-1",
      }),
    ).rejects.toThrow();

    await waitFor(() => {
      expect(toast.error).toHaveBeenCalledWith("Failed to resolve YNAB memo sync");
    });
  });

  it("useMemoSyncSummary computes correct summary", () => {
    const results = [
      { localTransactionId: "tx-1", receiptId: "r-1", outcome: "Synced" as const, ynabTransactionId: "yt-1" },
      { localTransactionId: "tx-2", receiptId: "r-1", outcome: "AlreadySynced" as const, ynabTransactionId: "yt-2" },
      { localTransactionId: "tx-3", receiptId: "r-1", outcome: "NoMatch" as const },
      { localTransactionId: "tx-4", receiptId: "r-1", outcome: "Ambiguous" as const, ambiguousCandidates: [] },
      { localTransactionId: "tx-5", receiptId: "r-1", outcome: "Failed" as const, error: "err" },
    ];

    const { result } = renderHook(() => useMemoSyncSummary(results), {
      wrapper: createWrapper(),
    });

    expect(result.current).toEqual({
      synced: 1,
      alreadySynced: 1,
      noMatch: 1,
      ambiguous: 1,
      currencySkipped: 0,
      reconciledSkipped: 0,
      failed: 1,
      total: 5,
    });
  });

  it("useMemoSyncSummary counts reconciledSkipped outcomes", () => {
    const results = [
      { localTransactionId: "tx-1", receiptId: "r-1", outcome: "ReconciledSkipped" as const, ynabTransactionId: "yt-1", error: "reconciled" },
      { localTransactionId: "tx-2", receiptId: "r-1", outcome: "Synced" as const, ynabTransactionId: "yt-2" },
    ];

    const { result } = renderHook(() => useMemoSyncSummary(results), {
      wrapper: createWrapper(),
    });

    expect(result.current).toEqual({
      synced: 1,
      alreadySynced: 0,
      noMatch: 0,
      ambiguous: 0,
      currencySkipped: 0,
      reconciledSkipped: 1,
      failed: 0,
      total: 2,
    });
  });

  it("useMemoSyncSummary returns null when no results", () => {
    const { result } = renderHook(() => useMemoSyncSummary(undefined), {
      wrapper: createWrapper(),
    });

    expect(result.current).toBeNull();
  });

  it("usePushYnabTransactions calls POST and shows success toast", async () => {
    const pushResult = {
      success: true,
      pushedTransactions: [
        {
          localTransactionId: "tx-1",
          ynabTransactionId: "ynab-tx-1",
          milliunits: -15000,
          subTransactionCount: 2,
        },
      ],
      unmappedCategories: null,
      error: null,
    };
    (client.POST as Mock).mockResolvedValue({
      data: pushResult,
      error: undefined,
    });

    const { result } = renderHook(() => usePushYnabTransactions(), {
      wrapper: createWrapper(),
    });

    await result.current.mutateAsync("receipt-123");

    expect(client.POST).toHaveBeenCalledWith("/api/ynab/push-transactions", {
      body: { receiptId: "receipt-123" },
    });
    expect(toast.success).toHaveBeenCalledWith(
      "Pushed 1 transaction(s) to YNAB",
    );
  });

  it("usePushYnabTransactions shows error toast on failure response", async () => {
    const pushResult = {
      success: false,
      pushedTransactions: [],
      unmappedCategories: ["Electronics"],
      error: "Unmapped categories found.",
    };
    (client.POST as Mock).mockResolvedValue({
      data: pushResult,
      error: undefined,
    });

    const { result } = renderHook(() => usePushYnabTransactions(), {
      wrapper: createWrapper(),
    });

    await result.current.mutateAsync("receipt-123");

    expect(toast.error).toHaveBeenCalledWith("Unmapped categories found.");
  });

  it("usePushYnabTransactions shows error toast on network failure", async () => {
    (client.POST as Mock).mockResolvedValue({
      error: "Network error",
    });

    const { result } = renderHook(() => usePushYnabTransactions(), {
      wrapper: createWrapper(),
    });

    await expect(result.current.mutateAsync("receipt-123")).rejects.toThrow();

    await waitFor(() => {
      expect(toast.error).toHaveBeenCalledWith(
        "Failed to push transactions to YNAB",
      );
    });
  });

  it("useBulkPushYnabTransactions calls POST and shows success toast", async () => {
    const bulkResult = {
      results: [
        {
          receiptId: "r1",
          result: { success: true, pushedTransactions: [], error: null },
        },
        {
          receiptId: "r2",
          result: { success: false, pushedTransactions: [], error: "Not found" },
        },
      ],
    };
    (client.POST as Mock).mockResolvedValue({
      data: bulkResult,
      error: undefined,
    });

    const { result } = renderHook(() => useBulkPushYnabTransactions(), {
      wrapper: createWrapper(),
    });

    await result.current.mutateAsync(["r1", "r2"]);

    expect(client.POST).toHaveBeenCalledWith(
      "/api/ynab/push-transactions/bulk",
      {
        body: { receiptIds: ["r1", "r2"] },
      },
    );
    expect(toast.success).toHaveBeenCalledWith(
      "Pushed 1/2 receipt(s) to YNAB",
    );
  });
});
