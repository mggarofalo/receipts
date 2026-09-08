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
  toast: { success: vi.fn(), error: vi.fn(), info: vi.fn(), warning: vi.fn() },
}));

import client from "@/lib/api-client";
import createClient, { type Middleware } from "openapi-fetch";
import { getRequestErrorPresentation } from "@/lib/request-error-policy";
import { toast } from "sonner";
import {
  useYnabConnectionStatus,
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
  useAllReceiptIds,
  useYnabRateLimitStatus,
  useStaleMappings,
  useClearStaleMappings,
  useReceiptYnabSyncStatuses,
  useYnabSplitComparison,
} from "./useYnab";

// Verify what the supplied middleware does on a real SDK Request, without
// coupling these endpoint assertions to a particular middleware object identity.
async function expectRequestOwnership(
  method: Mock,
  path: string,
  presentation: "local" | "toast",
  requiresSignal = presentation === "local",
) {
  const call = method.mock.calls.find(([url]) => url === path);
  const options = call?.[1] as {
    middleware?: Middleware[];
    signal?: AbortSignal;
  };
  expect(options.middleware).toEqual(
    expect.arrayContaining([
      expect.objectContaining({ onRequest: expect.any(Function) }),
    ]),
  );
  if (requiresSignal) expect(options.signal).toBeInstanceOf(AbortSignal);
  let observed: string | undefined;
  const probe = createClient<{
    "/probe": { get: { responses: { 204: { content?: never } } } };
  }>({
    baseUrl: "http://request-policy.test",
    fetch: async (request) => {
      observed = getRequestErrorPresentation(request);
      return new Response(null, { status: 204 });
    },
  });
  await probe.GET("/probe", { middleware: options.middleware });
  expect(observed).toBe(presentation);
}

function createWrapper() {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false, gcTime: 0 } },
  });
  return function Wrapper({ children }: { children: ReactNode }) {
    return createElement(
      QueryClientProvider,
      { client: queryClient },
      children,
    );
  };
}

beforeEach(() => {
  vi.clearAllMocks();
});

describe("useYnab", () => {
  it("useYnabConnectionStatus returns connection status on success", async () => {
    const status = {
      isConfigured: true,
      isConnected: true,
      lastSuccessfulSyncUtc: "2026-04-05T12:00:00Z",
    };
    (client.GET as Mock).mockResolvedValue({ data: status, error: undefined });

    const { result } = renderHook(() => useYnabConnectionStatus(), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(result.current.isConfigured).toBe(true);
    expect(result.current.isConnected).toBe(true);
    expect(result.current.lastSuccessfulSyncUtc).toBe("2026-04-05T12:00:00Z");
    expect(client.GET).toHaveBeenCalledWith(
      "/api/ynab/connection-status",
      expect.objectContaining({
        middleware: expect.any(Array),
        signal: expect.any(AbortSignal),
      }),
    );
    await expectRequestOwnership(
      client.GET as Mock,
      "/api/ynab/connection-status",
      "local",
    );
  });

  it("useYnabConnectionStatus returns defaults when data is undefined", async () => {
    (client.GET as Mock).mockResolvedValue({
      data: undefined,
      error: "Server error",
    });

    const { result } = renderHook(() => useYnabConnectionStatus(), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isError).toBe(true));
    expect(result.current.isConfigured).toBe(false);
    expect(result.current.isConnected).toBe(false);
    expect(result.current.lastSuccessfulSyncUtc).toBeNull();
  });

  it("useYnabBudgets returns budgets on success", async () => {
    const budgets = [
      { id: "budget-1", name: "My Budget" },
      { id: "budget-2", name: "Other Budget" },
    ];
    (client.GET as Mock).mockResolvedValue({
      data: { data: budgets },
      error: undefined,
    });

    const { result } = renderHook(() => useYnabBudgets(), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(result.current.budgets).toEqual(budgets);
    expect(client.GET).toHaveBeenCalledWith(
      "/api/ynab/budgets",
      expect.objectContaining({
        middleware: expect.any(Array),
        signal: expect.any(AbortSignal),
      }),
    );
    await expectRequestOwnership(
      client.GET as Mock,
      "/api/ynab/budgets",
      "local",
    );
  });

  it("useYnabBudgets returns empty array when data is undefined", async () => {
    (client.GET as Mock).mockResolvedValue({
      data: undefined,
      error: "Service unavailable",
    });

    const { result } = renderHook(() => useYnabBudgets(), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isError).toBe(true));
    expect(result.current.budgets).toEqual([]);
  });

  it("useSelectedYnabBudget returns selected budget id", async () => {
    const budgetId = "budget-123";
    (client.GET as Mock).mockResolvedValue({
      data: { selectedBudgetId: budgetId },
      error: undefined,
    });

    const { result } = renderHook(() => useSelectedYnabBudget(), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(result.current.selectedBudgetId).toBe(budgetId);
  });

  it("useSelectedYnabBudget returns null when no budget selected", async () => {
    (client.GET as Mock).mockResolvedValue({
      data: { selectedBudgetId: null },
      error: undefined,
    });

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

    expect(client.PUT).toHaveBeenCalledWith(
      "/api/ynab/settings/budget",
      expect.objectContaining({
        body: { budgetId: "budget-123" },
        middleware: expect.any(Array),
      }),
    );
    await expectRequestOwnership(
      client.PUT as Mock,
      "/api/ynab/settings/budget",
      "toast",
    );
    expect(toast.success).toHaveBeenCalledWith("YNAB budget selected");
  });

  it("useSelectYnabBudget does not add a hook toast on failure (owned by the mutation cache)", async () => {
    (client.PUT as Mock).mockResolvedValue({ error: "Failed" });

    const { result } = renderHook(() => useSelectYnabBudget(), {
      wrapper: createWrapper(),
    });

    await expect(result.current.mutateAsync("budget-123")).rejects.toThrow();

    await waitFor(() => {
      expect(toast.error).not.toHaveBeenCalled();
    });
  });

  it("useYnabAccounts returns accounts on success", async () => {
    const accounts = [
      {
        id: "acc-1",
        name: "Checking",
        type: "checking",
        onBudget: true,
        closed: false,
        balance: 100000,
      },
      {
        id: "acc-2",
        name: "Savings",
        type: "savings",
        onBudget: true,
        closed: false,
        balance: 50000,
      },
    ];
    (client.GET as Mock).mockResolvedValue({
      data: { data: accounts },
      error: undefined,
    });

    const { result } = renderHook(() => useYnabAccounts(), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(result.current.accounts).toEqual(accounts);
    expect(client.GET).toHaveBeenCalledWith(
      "/api/ynab/accounts",
      expect.objectContaining({
        middleware: expect.any(Array),
        signal: expect.any(AbortSignal),
      }),
    );
    await expectRequestOwnership(
      client.GET as Mock,
      "/api/ynab/accounts",
      "local",
    );
  });

  it("useYnabAccounts returns empty array on error", async () => {
    (client.GET as Mock).mockResolvedValue({
      data: undefined,
      error: "Service unavailable",
    });

    const { result } = renderHook(() => useYnabAccounts(), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isError).toBe(true));
    expect(result.current.accounts).toEqual([]);
  });

  it("useYnabAccountMappings returns mappings on success", async () => {
    const mappings = [
      {
        id: "m1",
        receiptsAccountId: "a1",
        ynabAccountId: "y1",
        ynabAccountName: "Checking",
        ynabBudgetId: "b1",
      },
    ];
    (client.GET as Mock).mockResolvedValue({
      data: { data: mappings },
      error: undefined,
    });

    const { result } = renderHook(() => useYnabAccountMappings(), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(result.current.mappings).toEqual(mappings);
    expect(client.GET).toHaveBeenCalledWith(
      "/api/ynab/account-mappings",
      expect.objectContaining({
        middleware: expect.any(Array),
        signal: expect.any(AbortSignal),
      }),
    );
    await expectRequestOwnership(
      client.GET as Mock,
      "/api/ynab/account-mappings",
      "local",
    );
  });

  it("useCreateYnabAccountMapping calls POST and shows toast", async () => {
    (client.POST as Mock).mockResolvedValue({
      data: { id: "new-id" },
      error: undefined,
    });

    const { result } = renderHook(() => useCreateYnabAccountMapping(), {
      wrapper: createWrapper(),
    });

    await result.current.mutateAsync({
      receiptsAccountId: "a1",
      ynabAccountId: "y1",
      ynabAccountName: "Checking",
      ynabBudgetId: "b1",
    });

    expect(client.POST).toHaveBeenCalledWith(
      "/api/ynab/account-mappings",
      expect.objectContaining({
        body: {
          receiptsAccountId: "a1",
          ynabAccountId: "y1",
          ynabAccountName: "Checking",
          ynabBudgetId: "b1",
        },
        middleware: expect.any(Array),
      }),
    );
    await expectRequestOwnership(
      client.POST as Mock,
      "/api/ynab/account-mappings",
      "toast",
    );
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

    expect(client.PUT).toHaveBeenCalledWith(
      "/api/ynab/account-mappings/{id}",
      expect.objectContaining({
        params: { path: { id: "m1" } },
        body: {
          ynabAccountId: "y2",
          ynabAccountName: "Savings",
          ynabBudgetId: "b1",
        },
        middleware: expect.any(Array),
      }),
    );
    await expectRequestOwnership(
      client.PUT as Mock,
      "/api/ynab/account-mappings/{id}",
      "toast",
    );
    expect(toast.success).toHaveBeenCalledWith("Account mapping updated");
  });

  it("useDeleteYnabAccountMapping calls DELETE and shows toast", async () => {
    (client.DELETE as Mock).mockResolvedValue({ error: undefined });

    const { result } = renderHook(() => useDeleteYnabAccountMapping(), {
      wrapper: createWrapper(),
    });

    await result.current.mutateAsync("m1");

    expect(client.DELETE).toHaveBeenCalledWith(
      "/api/ynab/account-mappings/{id}",
      expect.objectContaining({
        params: { path: { id: "m1" } },
        middleware: expect.any(Array),
      }),
    );
    await expectRequestOwnership(
      client.DELETE as Mock,
      "/api/ynab/account-mappings/{id}",
      "toast",
    );
    expect(toast.success).toHaveBeenCalledWith("Account mapping removed");
  });

  it("useCreateYnabAccountMapping does not add a hook toast on failure (owned by the mutation cache)", async () => {
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
      expect(toast.error).not.toHaveBeenCalled();
    });
  });

  it("useYnabCategories returns categories on success", async () => {
    const categories = [
      {
        id: "cat-1",
        name: "Groceries",
        categoryGroupId: "group-1",
        categoryGroupName: "Needs",
        hidden: false,
      },
      {
        id: "cat-2",
        name: "Rent",
        categoryGroupId: "group-1",
        categoryGroupName: "Needs",
        hidden: false,
      },
    ];
    (client.GET as Mock).mockResolvedValue({
      data: { data: categories },
      error: undefined,
    });

    const { result } = renderHook(() => useYnabCategories(), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(result.current.categories).toEqual(categories);
    expect(client.GET).toHaveBeenCalledWith(
      "/api/ynab/categories",
      expect.objectContaining({
        middleware: expect.any(Array),
        signal: expect.any(AbortSignal),
      }),
    );
    await expectRequestOwnership(
      client.GET as Mock,
      "/api/ynab/categories",
      "local",
    );
  });

  it("useYnabCategories returns empty array on error", async () => {
    (client.GET as Mock).mockResolvedValue({
      data: undefined,
      error: "Service unavailable",
    });

    const { result } = renderHook(() => useYnabCategories(), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isError).toBe(true));
    expect(result.current.categories).toEqual([]);
  });

  it("useDistinctReceiptItemCategories returns categories on success", async () => {
    const categories = ["Electronics", "Groceries", "Pharmacy"];
    (client.GET as Mock).mockResolvedValue({
      data: { categories },
      error: undefined,
    });

    const { result } = renderHook(() => useDistinctReceiptItemCategories(), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(result.current.categories).toEqual(categories);
    expect(client.GET).toHaveBeenCalledWith(
      "/api/receipt-items/distinct-categories",
      expect.objectContaining({
        middleware: expect.any(Array),
        signal: expect.any(AbortSignal),
      }),
    );
    await expectRequestOwnership(
      client.GET as Mock,
      "/api/receipt-items/distinct-categories",
      "local",
    );
  });

  it("useYnabCategoryMappings returns mappings on success", async () => {
    const mappings = [
      {
        id: "m-1",
        receiptsCategory: "Groceries",
        ynabCategoryId: "cat-1",
        ynabCategoryName: "Groceries",
        ynabCategoryGroupName: "Needs",
        ynabBudgetId: "budget-1",
        createdAt: "2024-01-01T00:00:00Z",
        updatedAt: "2024-01-01T00:00:00Z",
      },
    ];
    (client.GET as Mock).mockResolvedValue({
      data: { data: mappings },
      error: undefined,
    });

    const { result } = renderHook(() => useYnabCategoryMappings(), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(result.current.mappings).toEqual(mappings);
    expect(client.GET).toHaveBeenCalledWith(
      "/api/ynab/category-mappings",
      expect.objectContaining({
        middleware: expect.any(Array),
        signal: expect.any(AbortSignal),
      }),
    );
    await expectRequestOwnership(
      client.GET as Mock,
      "/api/ynab/category-mappings",
      "local",
    );
  });

  it("useUnmappedCategories returns unmapped list on success", async () => {
    const unmappedCategories = ["Electronics", "Pharmacy"];
    (client.GET as Mock).mockResolvedValue({
      data: { unmappedCategories },
      error: undefined,
    });

    const { result } = renderHook(() => useUnmappedCategories(), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(result.current.unmappedCategories).toEqual(unmappedCategories);
    expect(client.GET).toHaveBeenCalledWith(
      "/api/ynab/category-mappings/unmapped",
      expect.objectContaining({
        middleware: expect.any(Array),
        signal: expect.any(AbortSignal),
      }),
    );
    await expectRequestOwnership(
      client.GET as Mock,
      "/api/ynab/category-mappings/unmapped",
      "local",
    );
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

    expect(client.POST).toHaveBeenCalledWith(
      "/api/ynab/category-mappings",
      expect.objectContaining({
        body: {
          receiptsCategory: "Groceries",
          ynabCategoryId: "cat-1",
          ynabCategoryName: "Groceries",
          ynabCategoryGroupName: "Needs",
          ynabBudgetId: "budget-1",
        },
        middleware: expect.any(Array),
      }),
    );
    await expectRequestOwnership(
      client.POST as Mock,
      "/api/ynab/category-mappings",
      "toast",
    );
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

    expect(client.PUT).toHaveBeenCalledWith(
      "/api/ynab/category-mappings/{id}",
      expect.objectContaining({
        params: { path: { id: "m-1" } },
        body: {
          ynabCategoryId: "cat-2",
          ynabCategoryName: "Rent",
          ynabCategoryGroupName: "Needs",
          ynabBudgetId: "budget-1",
        },
        middleware: expect.any(Array),
      }),
    );
    await expectRequestOwnership(
      client.PUT as Mock,
      "/api/ynab/category-mappings/{id}",
      "toast",
    );
    expect(toast.success).toHaveBeenCalledWith("Category mapping updated");
  });

  it("useDeleteYnabCategoryMapping calls DELETE and shows toast on success", async () => {
    (client.DELETE as Mock).mockResolvedValue({ error: undefined });

    const { result } = renderHook(() => useDeleteYnabCategoryMapping(), {
      wrapper: createWrapper(),
    });

    await result.current.mutateAsync("m-1");

    expect(client.DELETE).toHaveBeenCalledWith(
      "/api/ynab/category-mappings/{id}",
      expect.objectContaining({
        params: { path: { id: "m-1" } },
        middleware: expect.any(Array),
      }),
    );
    await expectRequestOwnership(
      client.DELETE as Mock,
      "/api/ynab/category-mappings/{id}",
      "toast",
    );
    expect(toast.success).toHaveBeenCalledWith("Category mapping deleted");
  });

  it("useCreateYnabCategoryMapping does not add a hook toast on failure (owned by the mutation cache)", async () => {
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
      expect(toast.error).not.toHaveBeenCalled();
    });
  });

  it("useSyncYnabMemos calls POST and shows toast on success", async () => {
    const syncResults = {
      results: [
        {
          localTransactionId: "tx-1",
          receiptId: "r-1",
          outcome: "synced",
          ynabTransactionId: "yt-1",
        },
      ],
    };
    (client.POST as Mock).mockResolvedValue({
      data: syncResults,
      error: undefined,
    });

    const { result } = renderHook(() => useSyncYnabMemos(), {
      wrapper: createWrapper(),
    });

    await result.current.mutateAsync("r-1");

    expect(client.POST).toHaveBeenCalledWith("/api/ynab/sync-memos", {
      middleware: expect.any(Array),
      body: { receiptId: "r-1" },
    });
    await expectRequestOwnership(
      client.POST as Mock,
      "/api/ynab/sync-memos",
      "local",
      false,
    );
    expect(toast.success).toHaveBeenCalledWith(
      "Synced 1 transaction memo(s) to YNAB",
    );
  });

  it("useSyncYnabMemos shows info toast when no transactions synced", async () => {
    const syncResults = {
      results: [
        { localTransactionId: "tx-1", receiptId: "r-1", outcome: "noMatch" },
      ],
    };
    (client.POST as Mock).mockResolvedValue({
      data: syncResults,
      error: undefined,
    });

    const { result } = renderHook(() => useSyncYnabMemos(), {
      wrapper: createWrapper(),
    });

    await result.current.mutateAsync("r-1");

    expect(toast.info).toHaveBeenCalledWith("No transactions were synced");
  });

  it("useSyncYnabMemos does not add a hook toast on transport failure (owned by the component)", async () => {
    (client.POST as Mock).mockResolvedValue({ error: "Server error" });

    const { result } = renderHook(() => useSyncYnabMemos(), {
      wrapper: createWrapper(),
    });

    await expect(result.current.mutateAsync("r-1")).rejects.toThrow();

    await waitFor(() => {
      expect(toast.error).not.toHaveBeenCalled();
    });
  });

  it("useSyncYnabMemosBulk calls POST and shows toast on success", async () => {
    const syncResults = {
      results: [
        {
          localTransactionId: "tx-1",
          receiptId: "r-1",
          outcome: "synced",
          ynabTransactionId: "yt-1",
        },
        {
          localTransactionId: "tx-2",
          receiptId: "r-2",
          outcome: "synced",
          ynabTransactionId: "yt-2",
        },
      ],
    };
    (client.POST as Mock).mockResolvedValue({
      data: syncResults,
      error: undefined,
    });

    const { result } = renderHook(() => useSyncYnabMemosBulk(), {
      wrapper: createWrapper(),
    });

    await result.current.mutateAsync(["r-1", "r-2"]);

    expect(client.POST).toHaveBeenCalledWith("/api/ynab/sync-memos/bulk", {
      middleware: expect.any(Array),
      body: { receiptIds: ["r-1", "r-2"] },
    });
    await expectRequestOwnership(
      client.POST as Mock,
      "/api/ynab/sync-memos/bulk",
      "local",
      false,
    );
    expect(toast.success).toHaveBeenCalledWith(
      "Synced 2 transaction memo(s) to YNAB",
    );
  });

  it("useSyncYnabMemosBulk does not add a hook toast on transport failure (owned by the component)", async () => {
    (client.POST as Mock).mockResolvedValue({ error: "Server error" });

    const { result } = renderHook(() => useSyncYnabMemosBulk(), {
      wrapper: createWrapper(),
    });

    await expect(result.current.mutateAsync(["r-1"])).rejects.toThrow();

    await waitFor(() => {
      expect(toast.error).not.toHaveBeenCalled();
    });
  });

  it("useResolveYnabMemoSync calls POST and shows toast on success", async () => {
    const resolved = {
      localTransactionId: "tx-1",
      receiptId: "r-1",
      outcome: "synced",
      ynabTransactionId: "yt-1",
    };
    (client.POST as Mock).mockResolvedValue({
      data: resolved,
      error: undefined,
    });

    const { result } = renderHook(() => useResolveYnabMemoSync(), {
      wrapper: createWrapper(),
    });

    await result.current.mutateAsync({
      localTransactionId: "tx-1",
      ynabTransactionId: "yt-1",
    });

    expect(client.POST).toHaveBeenCalledWith("/api/ynab/sync-memos/resolve", {
      middleware: expect.any(Array),
      body: { localTransactionId: "tx-1", ynabTransactionId: "yt-1" },
    });
    await expectRequestOwnership(
      client.POST as Mock,
      "/api/ynab/sync-memos/resolve",
      "local",
      false,
    );
    expect(toast.success).toHaveBeenCalledWith("YNAB memo sync resolved");
  });

  it("useResolveYnabMemoSync does not add a hook toast on transport failure (owned by the component)", async () => {
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
      expect(toast.error).not.toHaveBeenCalled();
    });
  });

  it("useMemoSyncSummary computes correct summary", () => {
    const results = [
      {
        localTransactionId: "tx-1",
        receiptId: "r-1",
        outcome: "synced" as const,
        ynabTransactionId: "yt-1",
      },
      {
        localTransactionId: "tx-2",
        receiptId: "r-1",
        outcome: "alreadySynced" as const,
        ynabTransactionId: "yt-2",
      },
      {
        localTransactionId: "tx-3",
        receiptId: "r-1",
        outcome: "noMatch" as const,
      },
      {
        localTransactionId: "tx-4",
        receiptId: "r-1",
        outcome: "ambiguous" as const,
        ambiguousCandidates: [],
      },
      {
        localTransactionId: "tx-5",
        receiptId: "r-1",
        outcome: "failed" as const,
        error: "err",
      },
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
      {
        localTransactionId: "tx-1",
        receiptId: "r-1",
        outcome: "reconciledSkipped" as const,
        ynabTransactionId: "yt-1",
        error: "reconciled",
      },
      {
        localTransactionId: "tx-2",
        receiptId: "r-1",
        outcome: "synced" as const,
        ynabTransactionId: "yt-2",
      },
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
      middleware: expect.any(Array),
      body: { receiptId: "receipt-123" },
    });
    await expectRequestOwnership(
      client.POST as Mock,
      "/api/ynab/push-transactions",
      "local",
      false,
    );
    expect(toast.success).toHaveBeenCalledWith(
      "Pushed 1 transaction(s) to YNAB",
    );
  });

  it("usePushYnabTransactions does not add a hook toast on transport failure (owned by the component) response", async () => {
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

  it("usePushYnabTransactions does not toast on network failure (surfaced by the global handler)", async () => {
    (client.POST as Mock).mockResolvedValue({
      error: "Network error",
    });

    const { result } = renderHook(() => usePushYnabTransactions(), {
      wrapper: createWrapper(),
    });

    await expect(result.current.mutateAsync("receipt-123")).rejects.toThrow();

    await waitFor(() => {
      expect(toast.error).not.toHaveBeenCalled();
    });
  });

  it("useBulkPushYnabTransactions warns (not success) on partial success and lists unmapped categories", async () => {
    const bulkResult = {
      results: [
        {
          receiptId: "r1",
          result: { success: true, pushedTransactions: [], error: null },
        },
        {
          receiptId: "r2",
          result: {
            success: false,
            pushedTransactions: [],
            error: "Unmapped categories",
            unmappedCategories: ["Gas", "Dining"],
          },
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
        middleware: expect.any(Array),
        body: { receiptIds: ["r1", "r2"] },
      },
    );
    await expectRequestOwnership(
      client.POST as Mock,
      "/api/ynab/push-transactions/bulk",
      "local",
      false,
    );
    // Partial success must NOT read as a plain success (RECEIPTS-783).
    expect(toast.success).not.toHaveBeenCalled();
    expect(toast.warning).toHaveBeenCalledWith(
      "Pushed 1/2 receipt(s); 1 failed. Unmapped categories: Gas, Dining. Map them in YNAB Settings.",
    );
  });

  it("useBulkPushYnabTransactions shows an error toast when every receipt fails", async () => {
    const bulkResult = {
      results: [
        {
          receiptId: "r1",
          result: {
            success: false,
            pushedTransactions: [],
            error: "Unmapped categories",
            unmappedCategories: ["Gas"],
          },
        },
        {
          receiptId: "r2",
          result: {
            success: false,
            pushedTransactions: [],
            error: "Unmapped categories",
            unmappedCategories: ["Gas", "Fuel"],
          },
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

    expect(toast.success).not.toHaveBeenCalled();
    expect(toast.warning).not.toHaveBeenCalled();
    // Distinct unmapped categories aggregated across all failed receipts.
    expect(toast.error).toHaveBeenCalledWith(
      "Failed to push 2 receipt(s) to YNAB. Unmapped categories: Gas, Fuel. Map them in YNAB Settings.",
    );
  });

  it("useBulkPushYnabTransactions shows a success toast when every receipt succeeds", async () => {
    const bulkResult = {
      results: [
        {
          receiptId: "r1",
          result: { success: true, pushedTransactions: [], error: null },
        },
        {
          receiptId: "r2",
          result: { success: true, pushedTransactions: [], error: null },
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

    expect(toast.success).toHaveBeenCalledWith("Pushed 2/2 receipt(s) to YNAB");
    expect(toast.warning).not.toHaveBeenCalled();
    expect(toast.error).not.toHaveBeenCalled();
  });

  it("useAllReceiptIds returns receipt IDs from a single page", async () => {
    const receipts = [
      { id: "r1", location: "Store A", date: "2024-01-01", taxAmount: 5 },
      { id: "r2", location: "Store B", date: "2024-01-02", taxAmount: 3 },
    ];
    (client.GET as Mock).mockResolvedValue({
      data: { data: receipts, total: 2, offset: 0, limit: 500 },
      error: undefined,
    });

    const { result } = renderHook(() => useAllReceiptIds(), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(result.current.receiptIds).toEqual(["r1", "r2"]);
    expect(result.current.totalReceipts).toBe(2);
    expect(result.current.isTruncated).toBe(false);
    expect(client.GET).toHaveBeenCalledWith("/api/receipts", {
      params: { query: { offset: 0, limit: 500 } },
    });
  });

  it("useAllReceiptIds paginates through multiple pages", async () => {
    const page1 = Array.from({ length: 500 }, (_, i) => ({
      id: `r${i}`,
      location: "Store",
      date: "2024-01-01",
      taxAmount: 0,
    }));
    const page2 = [
      { id: "r500", location: "Store", date: "2024-01-01", taxAmount: 0 },
      { id: "r501", location: "Store", date: "2024-01-01", taxAmount: 0 },
    ];

    (client.GET as Mock)
      .mockResolvedValueOnce({
        data: { data: page1, total: 502, offset: 0, limit: 500 },
        error: undefined,
      })
      .mockResolvedValueOnce({
        data: { data: page2, total: 502, offset: 500, limit: 500 },
        error: undefined,
      });

    const { result } = renderHook(() => useAllReceiptIds(), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(result.current.receiptIds).toHaveLength(502);
    expect(result.current.totalReceipts).toBe(502);
    expect(result.current.isTruncated).toBe(false);
    expect(client.GET).toHaveBeenCalledTimes(2);
    expect(client.GET).toHaveBeenCalledWith("/api/receipts", {
      params: { query: { offset: 0, limit: 500 } },
    });
    expect(client.GET).toHaveBeenCalledWith("/api/receipts", {
      params: { query: { offset: 500, limit: 500 } },
    });
  });

  it("useAllReceiptIds returns empty array when data is undefined", async () => {
    (client.GET as Mock).mockResolvedValue({
      data: undefined,
      error: "Server error",
    });

    const { result } = renderHook(() => useAllReceiptIds(), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isError).toBe(true));
    expect(result.current.receiptIds).toEqual([]);
    expect(result.current.totalReceipts).toBe(0);
    expect(result.current.isTruncated).toBe(false);
  });

  it("useAllReceiptIds respects enabled flag", () => {
    const { result } = renderHook(() => useAllReceiptIds(false), {
      wrapper: createWrapper(),
    });

    expect(result.current.receiptIds).toEqual([]);
    expect(result.current.isTruncated).toBe(false);
    expect(client.GET).not.toHaveBeenCalled();
  });

  it("useReceiptYnabSyncStatuses returns status map on success", async () => {
    const statuses = {
      data: [
        { receiptId: "r1", syncStatus: "synced" },
        { receiptId: "r2", syncStatus: "failed" },
        { receiptId: "r3", syncStatus: "notSynced" },
      ],
    };
    (client.GET as Mock).mockResolvedValue({
      data: statuses,
      response: Response.json(statuses),
    });

    const { result } = renderHook(
      () => useReceiptYnabSyncStatuses(["r1", "r2", "r3"]),
      { wrapper: createWrapper() },
    );

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(result.current.statusMap.get("r1")).toBe("synced");
    expect(result.current.statusMap.get("r2")).toBe("failed");
    expect(result.current.statusMap.get("r3")).toBe("notSynced");
    expect(client.GET).toHaveBeenCalledWith("/api/ynab/receipt-sync-statuses", {
      middleware: expect.any(Array),
      params: { query: { receiptIds: ["r1", "r2", "r3"] } },
    });
  });

  it("useReceiptYnabSyncStatuses exposes a failed lookup instead of reporting a successful empty map", async () => {
    (client.GET as Mock).mockResolvedValue({
      data: undefined,
      error: { status: 503, detail: "Sync status unavailable" },
    });

    const { result } = renderHook(() => useReceiptYnabSyncStatuses(["r1"]), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isError).toBe(true));
    expect(result.current.error).toEqual({
      status: 503,
      detail: "Sync status unavailable",
    });
    expect(result.current.statusMap.size).toBe(0);
  });

  it("useReceiptYnabSyncStatuses is disabled when receiptIds is empty", () => {
    const { result } = renderHook(() => useReceiptYnabSyncStatuses([]), {
      wrapper: createWrapper(),
    });

    expect(result.current.statusMap.size).toBe(0);
    expect(client.GET).not.toHaveBeenCalledWith(
      "/api/ynab/receipt-sync-statuses",
      expect.anything(),
    );
  });

  it("useReceiptYnabSyncStatuses suppresses the request when disabled", () => {
    const { result } = renderHook(
      () => useReceiptYnabSyncStatuses(["r1"], false),
      { wrapper: createWrapper() },
    );

    expect(result.current.statusMap.size).toBe(0);
    expect(client.GET).not.toHaveBeenCalledWith(
      "/api/ynab/receipt-sync-statuses",
      expect.anything(),
    );
  });

  it("useStaleMappings returns stale counts on success", async () => {
    (client.GET as Mock).mockResolvedValue({
      data: {
        staleAccountMappingCount: 2,
        staleCategoryMappingCount: 3,
        currentBudgetId: "budget-1",
      },
      error: undefined,
    });

    const { result } = renderHook(() => useStaleMappings(), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(result.current.staleAccountMappingCount).toBe(2);
    expect(result.current.staleCategoryMappingCount).toBe(3);
    expect(result.current.hasStaleMappings).toBe(true);
    expect(client.GET).toHaveBeenCalledWith(
      "/api/ynab/stale-mappings",
      expect.objectContaining({
        middleware: expect.any(Array),
        signal: expect.any(AbortSignal),
      }),
    );
    await expectRequestOwnership(
      client.GET as Mock,
      "/api/ynab/stale-mappings",
      "local",
    );
  });

  it("useStaleMappings returns hasStaleMappings false when no stale mappings", async () => {
    (client.GET as Mock).mockResolvedValue({
      data: {
        staleAccountMappingCount: 0,
        staleCategoryMappingCount: 0,
        currentBudgetId: "budget-1",
      },
      error: undefined,
    });

    const { result } = renderHook(() => useStaleMappings(), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(result.current.hasStaleMappings).toBe(false);
  });

  it("useStaleMappings returns defaults when data is undefined", async () => {
    (client.GET as Mock).mockResolvedValue({
      data: undefined,
      error: "Failed",
    });

    const { result } = renderHook(() => useStaleMappings(), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isError).toBe(true));
    expect(result.current.staleAccountMappingCount).toBe(0);
    expect(result.current.staleCategoryMappingCount).toBe(0);
    expect(result.current.hasStaleMappings).toBe(false);
  });

  it("useClearStaleMappings calls DELETE endpoint and shows toast", async () => {
    (client.DELETE as Mock).mockResolvedValue({
      data: { deletedAccountMappings: 2, deletedCategoryMappings: 3 },
      error: undefined,
    });

    const { result } = renderHook(() => useClearStaleMappings(), {
      wrapper: createWrapper(),
    });

    await result.current.mutateAsync();

    expect(client.DELETE).toHaveBeenCalledWith(
      "/api/ynab/stale-mappings",
      expect.objectContaining({ middleware: expect.any(Array) }),
    );
    await expectRequestOwnership(
      client.DELETE as Mock,
      "/api/ynab/stale-mappings",
      "toast",
    );
    expect(toast.success).toHaveBeenCalledWith("Cleared 5 stale mapping(s)");
  });

  it("useClearStaleMappings does not add a hook toast on failure (owned by the mutation cache)", async () => {
    (client.DELETE as Mock).mockResolvedValue({
      data: undefined,
      error: "Server error",
    });

    const { result } = renderHook(() => useClearStaleMappings(), {
      wrapper: createWrapper(),
    });

    await expect(result.current.mutateAsync()).rejects.toBeDefined();
    expect(toast.error).not.toHaveBeenCalled();
  });

  it("useYnabRateLimitStatus returns rate limit data on success", async () => {
    const rateLimitData = {
      remainingRequests: 150,
      maxRequests: 200,
      requestsUsed: 50,
      windowResetAt: "2026-04-05T23:00:00Z",
      oldestRequestAt: "2026-04-05T22:00:00Z",
    };
    (client.GET as Mock).mockResolvedValue({
      data: rateLimitData,
      error: undefined,
    });

    const { result } = renderHook(() => useYnabRateLimitStatus(), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(result.current.rateLimitStatus).toEqual(rateLimitData);
    expect(client.GET).toHaveBeenCalledWith(
      "/api/ynab/rate-limit-status",
      expect.objectContaining({
        middleware: expect.any(Array),
        signal: expect.any(AbortSignal),
      }),
    );
    await expectRequestOwnership(
      client.GET as Mock,
      "/api/ynab/rate-limit-status",
      "local",
    );
  });

  it("useYnabRateLimitStatus returns null when data is undefined", async () => {
    (client.GET as Mock).mockResolvedValue({ data: undefined, error: "Error" });

    const { result } = renderHook(() => useYnabRateLimitStatus(), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isError).toBe(true));
    expect(result.current.rateLimitStatus).toBeNull();
  });

  it("useYnabSplitComparison returns split comparison on success", async () => {
    const response = {
      canComputeExpected: true,
      expectedUnavailableReason: null,
      unmappedCategories: [],
      transactionComparisons: [
        {
          localTransactionId: "tx-1",
          accountName: "Checking",
          totalMilliunits: -11000,
          expected: [
            {
              ynabCategoryId: "cat-1",
              categoryName: "Groceries",
              milliunits: -11000,
            },
          ],
          actual: null,
          actualFetchError: null,
          matches: null,
        },
      ],
    };
    (client.GET as Mock).mockResolvedValue({
      data: response,
      error: undefined,
    });

    const { result } = renderHook(() => useYnabSplitComparison("receipt-1"), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(result.current.data).toEqual(response);
    expect(client.GET).toHaveBeenCalledWith(
      "/api/ynab/receipts/{receiptId}/split-comparison",
      {
        middleware: expect.any(Array),
        params: { path: { receiptId: "receipt-1" } },
      },
    );
  });

  it("useYnabSplitComparison is disabled when receiptId is undefined", () => {
    const { result } = renderHook(() => useYnabSplitComparison(undefined), {
      wrapper: createWrapper(),
    });

    expect(result.current.isPending).toBe(true);
    expect(result.current.fetchStatus).toBe("idle");
    expect(client.GET).not.toHaveBeenCalled();
  });

  it("useYnabSplitComparison surfaces an error when the request fails", async () => {
    (client.GET as Mock).mockResolvedValue({ data: undefined, error: "Boom" });

    const { result } = renderHook(() => useYnabSplitComparison("receipt-1"), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isError).toBe(true));
    expect(result.current.error).toBeDefined();
  });
});
