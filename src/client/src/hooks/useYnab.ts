import { useMemo } from "react";
import { useQuery, useMutation, useQueryClient } from "@tanstack/react-query";
import client from "@/lib/api-client";
import { toast } from "sonner";
// Some schemas exceed TypeScript's type-resolution depth when accessed via
// components["schemas"] due to the large union of 150+ schemas.  We define
// them inline (matching the generated api.d.ts) so property access is sound.

type YnabConnectionStatusResponse = {
  isConfigured: boolean;
  isConnected: boolean;
  lastSuccessfulSyncUtc?: string | null;
};

type StaleMappingsResponse = {
  staleAccountMappingCount: number;
  staleCategoryMappingCount: number;
  currentBudgetId?: string | null;
};

export function useYnabConnectionStatus() {
  const query = useQuery({
    queryKey: ["ynab", "connection-status"],
    staleTime: 5 * 60 * 1000, // 5 min — matches backend budget cache TTL
    queryFn: async () => {
      const { data, error } = await client.GET(
        "/api/ynab/connection-status" as never,
        {} as never,
      );
      if (error) throw error;
      return data as unknown as YnabConnectionStatusResponse;
    },
  });
  return useMemo(
    () => ({
      ...query,
      isConfigured: query.data?.isConfigured ?? false,
      isConnected: query.data?.isConnected ?? false,
      lastSuccessfulSyncUtc: query.data?.lastSuccessfulSyncUtc ?? null,
    }),
    [query],
  );
}

export function useYnabBudgets() {
  const query = useQuery({
    queryKey: ["ynab", "budgets"],
    queryFn: async () => {
      const { data, error } = await client.GET("/api/ynab/budgets");
      if (error) throw error;
      return data;
    },
    retry: false,
  });
  return useMemo(
    () => ({ ...query, budgets: query.data?.data ?? [] }),
    [query],
  );
}

export function useSelectedYnabBudget() {
  const query = useQuery({
    queryKey: ["ynab", "settings", "budget"],
    queryFn: async () => {
      const { data, error } = await client.GET("/api/ynab/settings/budget");
      if (error) throw error;
      return data;
    },
  });
  return useMemo(
    () => ({
      ...query,
      selectedBudgetId: query.data?.selectedBudgetId ?? null,
    }),
    [query],
  );
}

export function useSelectYnabBudget() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async (budgetId: string) => {
      const { error } = await client.PUT("/api/ynab/settings/budget", {
        body: { budgetId },
      });
      if (error) throw error;
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["ynab"] });
      toast.success("YNAB budget selected");
    },
    onError: () => {
      toast.error("Failed to select YNAB budget");
    },
  });
}

export function useYnabAccounts(enabled = true) {
  const query = useQuery({
    queryKey: ["ynab", "accounts"],
    queryFn: async () => {
      const { data, error } = await client.GET("/api/ynab/accounts");
      if (error) throw error;
      return data;
    },
    retry: false,
    enabled,
  });
  return useMemo(
    () => ({ ...query, accounts: query.data?.data ?? [] }),
    [query],
  );
}

export function useYnabAccountMappings(enabled = true) {
  const query = useQuery({
    queryKey: ["ynab", "account-mappings"],
    queryFn: async () => {
      const { data, error } = await client.GET("/api/ynab/account-mappings");
      if (error) throw error;
      return data;
    },
    enabled,
  });
  return useMemo(
    () => ({ ...query, mappings: query.data?.data ?? [] }),
    [query],
  );
}

export function useCreateYnabAccountMapping() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async (body: {
      receiptsAccountId: string;
      ynabAccountId: string;
      ynabAccountName: string;
      ynabBudgetId: string;
    }) => {
      const { data, error } = await client.POST(
        "/api/ynab/account-mappings",
        { body },
      );
      if (error) throw error;
      return data;
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["ynab", "account-mappings"] });
      toast.success("Account mapping created");
    },
    onError: () => {
      toast.error("Failed to create account mapping");
    },
  });
}

export function useUpdateYnabAccountMapping() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async (params: {
      id: string;
      ynabAccountId: string;
      ynabAccountName: string;
      ynabBudgetId: string;
    }) => {
      const { error } = await client.PUT(
        "/api/ynab/account-mappings/{id}",
        {
          params: { path: { id: params.id } },
          body: {
            ynabAccountId: params.ynabAccountId,
            ynabAccountName: params.ynabAccountName,
            ynabBudgetId: params.ynabBudgetId,
          },
        },
      );
      if (error) throw error;
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["ynab", "account-mappings"] });
      toast.success("Account mapping updated");
    },
    onError: () => {
      toast.error("Failed to update account mapping");
    },
  });
}

export function useDeleteYnabAccountMapping() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async (id: string) => {
      const { error } = await client.DELETE(
        "/api/ynab/account-mappings/{id}",
        {
          params: { path: { id } },
        },
      );
      if (error) throw error;
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["ynab", "account-mappings"] });
      toast.success("Account mapping removed");
    },
    onError: () => {
      toast.error("Failed to remove account mapping");
    },
  });
}

export function useYnabCategories(enabled = true) {
  const query = useQuery({
    queryKey: ["ynab", "categories"],
    queryFn: async () => {
      const { data, error } = await client.GET("/api/ynab/categories");
      if (error) throw error;
      return data;
    },
    retry: false,
    enabled,
  });
  return useMemo(
    () => ({ ...query, categories: query.data?.data ?? [] }),
    [query],
  );
}

export function useDistinctReceiptItemCategories(enabled = true) {
  const query = useQuery({
    queryKey: ["receipt-items", "distinct-categories"],
    queryFn: async () => {
      const { data, error } = await client.GET(
        "/api/receipt-items/distinct-categories",
      );
      if (error) throw error;
      return data;
    },
    enabled,
  });
  return useMemo(
    () => ({ ...query, categories: query.data?.categories ?? [] }),
    [query],
  );
}

export function useYnabCategoryMappings(enabled = true) {
  const query = useQuery({
    queryKey: ["ynab", "category-mappings"],
    queryFn: async () => {
      const { data, error } = await client.GET(
        "/api/ynab/category-mappings",
      );
      if (error) throw error;
      return data;
    },
    enabled,
  });
  return useMemo(
    () => ({ ...query, mappings: query.data?.data ?? [] }),
    [query],
  );
}

export function useUnmappedCategories(enabled = true) {
  const query = useQuery({
    queryKey: ["ynab", "category-mappings", "unmapped"],
    queryFn: async () => {
      const { data, error } = await client.GET(
        "/api/ynab/category-mappings/unmapped",
      );
      if (error) throw error;
      return data;
    },
    enabled,
  });
  return useMemo(
    () => ({
      ...query,
      unmappedCategories: query.data?.unmappedCategories ?? [],
    }),
    [query],
  );
}

export function useCreateYnabCategoryMapping() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async (body: {
      receiptsCategory: string;
      ynabCategoryId: string;
      ynabCategoryName: string;
      ynabCategoryGroupName: string;
      ynabBudgetId: string;
    }) => {
      const { data, error } = await client.POST(
        "/api/ynab/category-mappings",
        { body },
      );
      if (error) throw error;
      return data;
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["ynab", "category-mappings"] });
      toast.success("Category mapping created");
    },
    onError: () => {
      toast.error("Failed to create category mapping");
    },
  });
}

export function useUpdateYnabCategoryMapping() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async ({
      id,
      ...body
    }: {
      id: string;
      ynabCategoryId: string;
      ynabCategoryName: string;
      ynabCategoryGroupName: string;
      ynabBudgetId: string;
    }) => {
      const { error } = await client.PUT(
        "/api/ynab/category-mappings/{id}",
        { params: { path: { id } }, body },
      );
      if (error) throw error;
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["ynab", "category-mappings"] });
      toast.success("Category mapping updated");
    },
    onError: () => {
      toast.error("Failed to update category mapping");
    },
  });
}

export function useStaleMappings(enabled = true) {
  const query = useQuery({
    queryKey: ["ynab", "stale-mappings"],
    queryFn: async () => {
      const { data, error } = await client.GET(
        "/api/ynab/stale-mappings" as never,
        {} as never,
      );
      if (error) throw error;
      return data as unknown as StaleMappingsResponse;
    },
    enabled,
  });
  return useMemo(
    () => ({
      ...query,
      staleAccountMappingCount: query.data?.staleAccountMappingCount ?? 0,
      staleCategoryMappingCount: query.data?.staleCategoryMappingCount ?? 0,
      hasStaleMappings:
        (query.data?.staleAccountMappingCount ?? 0) > 0 ||
        (query.data?.staleCategoryMappingCount ?? 0) > 0,
    }),
    [query],
  );
}

type ClearStaleMappingsResponse = {
  deletedAccountMappings: number;
  deletedCategoryMappings: number;
};

export function useClearStaleMappings() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async () => {
      const { data, error } = await client.DELETE(
        "/api/ynab/stale-mappings" as never,
        {} as never,
      );
      if (error) throw error;
      return data as unknown as ClearStaleMappingsResponse;
    },
    onSuccess: (data) => {
      queryClient.invalidateQueries({ queryKey: ["ynab"] });
      const total =
        (data?.deletedAccountMappings ?? 0) +
        (data?.deletedCategoryMappings ?? 0);
      toast.success(`Cleared ${total} stale mapping(s)`);
    },
    onError: () => {
      toast.error("Failed to clear stale mappings");
    },
  });
}

export function useDeleteYnabCategoryMapping() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async (id: string) => {
      const { error } = await client.DELETE(
        "/api/ynab/category-mappings/{id}",
        { params: { path: { id } } },
      );
      if (error) throw error;
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["ynab", "category-mappings"] });
      toast.success("Category mapping deleted");
    },
    onError: () => {
      toast.error("Failed to delete category mapping");
    },
  });
}

export type YnabMemoSyncOutcome =
  | "Synced"
  | "AlreadySynced"
  | "NoMatch"
  | "Ambiguous"
  | "CurrencySkipped"
  | "ReconciledSkipped"
  | "Failed";

export type YnabTransactionCandidateDto = {
  id: string;
  date: string;
  amount: number;
  memo?: null | string;
  payeeName?: null | string;
  accountId: string;
};

export type YnabMemoSyncResult = {
  localTransactionId: string;
  receiptId: string;
  outcome: YnabMemoSyncOutcome;
  ynabTransactionId?: null | string;
  error?: null | string;
  ambiguousCandidates?: null | YnabTransactionCandidateDto[];
};

type YnabMemoSyncResponse = {
  results: YnabMemoSyncResult[];
};

type PushedTransactionInfo = {
  localTransactionId: string;
  ynabTransactionId: string;
  milliunits: number;
  subTransactionCount: number;
};

type PushYnabTransactionsResponse = {
  success: boolean;
  pushedTransactions: PushedTransactionInfo[];
  unmappedCategories?: null | string[];
  error?: null | string;
};

type BulkPushYnabTransactionsResponse = {
  results: {
    receiptId: string;
    result: PushYnabTransactionsResponse;
  }[];
};

export function useSyncYnabMemos() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async (receiptId: string) => {
      const { data, error } = await client.POST("/api/ynab/sync-memos", {
        body: { receiptId },
      });
      if (error) throw error;
      return data as unknown as YnabMemoSyncResponse;
    },
    onSuccess: (data) => {
      queryClient.invalidateQueries({ queryKey: ["ynab", "sync-status"] });
      queryClient.invalidateQueries({ queryKey: ["ynab", "receipt-sync-statuses"] });
      const synced = data?.results?.filter(
        (r) => r.outcome === "Synced",
      ).length;
      if (synced && synced > 0) {
        toast.success(`Synced ${synced} transaction memo(s) to YNAB`);
      } else {
        toast.info("No transactions were synced");
      }
    },
    onError: () => {
      toast.error("Failed to sync YNAB memos");
    },
  });
}

export function useSyncYnabMemosBulk() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async (receiptIds: string[]) => {
      const { data, error } = await client.POST("/api/ynab/sync-memos/bulk", {
        body: { receiptIds },
      });
      if (error) throw error;
      return data as unknown as YnabMemoSyncResponse;
    },
    onSuccess: (data) => {
      queryClient.invalidateQueries({ queryKey: ["ynab", "sync-status"] });
      queryClient.invalidateQueries({ queryKey: ["ynab", "receipt-sync-statuses"] });
      const synced = data?.results?.filter(
        (r) => r.outcome === "Synced",
      ).length;
      toast.success(`Synced ${synced ?? 0} transaction memo(s) to YNAB`);
    },
    onError: () => {
      toast.error("Failed to bulk sync YNAB memos");
    },
  });
}

export function useResolveYnabMemoSync() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async (params: {
      localTransactionId: string;
      ynabTransactionId: string;
    }) => {
      const { data, error } = await client.POST(
        "/api/ynab/sync-memos/resolve",
        { body: params },
      );
      if (error) throw error;
      return data;
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["ynab", "sync-status"] });
      queryClient.invalidateQueries({ queryKey: ["ynab", "receipt-sync-statuses"] });
      toast.success("YNAB memo sync resolved");
    },
    onError: () => {
      toast.error("Failed to resolve YNAB memo sync");
    },
  });
}

export function useMemoSyncSummary(results: YnabMemoSyncResult[] | undefined) {
  return useMemo(() => {
    if (!results) return null;
    return {
      synced: results.filter((r) => r.outcome === "Synced").length,
      alreadySynced: results.filter((r) => r.outcome === "AlreadySynced")
        .length,
      noMatch: results.filter((r) => r.outcome === "NoMatch").length,
      ambiguous: results.filter((r) => r.outcome === "Ambiguous").length,
      currencySkipped: results.filter((r) => r.outcome === "CurrencySkipped")
        .length,
      reconciledSkipped: results.filter(
        (r) => r.outcome === "ReconciledSkipped",
      ).length,
      failed: results.filter((r) => r.outcome === "Failed").length,
      total: results.length,
    };
  }, [results]);
}

export type SplitLineDto = {
  ynabCategoryId: string;
  categoryName: string;
  milliunits: number;
};

export type TransactionSplitComparisonDto = {
  localTransactionId: string;
  accountName: string;
  totalMilliunits: number;
  expected: SplitLineDto[];
  actual?: SplitLineDto[] | null;
  actualFetchError?: string | null;
  matches?: boolean | null;
};

export type ReceiptYnabSplitComparisonResponse = {
  canComputeExpected: boolean;
  expectedUnavailableReason?: string | null;
  unmappedCategories: string[];
  transactionComparisons: TransactionSplitComparisonDto[];
};

/**
 * Fetches the YNAB split comparison for a receipt.
 *
 * `enabled` should be set to whether YNAB is configured: the endpoint is
 * guarded by `[RequireYnabConfigured]` and returns 503 when YNAB is not set
 * up, so callers must not fire this query unless a connection exists.
 */
export function useYnabSplitComparison(
  receiptId: string | undefined,
  enabled = true,
) {
  return useQuery({
    queryKey: ["ynab", "split-comparison", receiptId],
    queryFn: async (): Promise<ReceiptYnabSplitComparisonResponse> => {
      const { data, error } = await client.GET(
        "/api/ynab/receipts/{receiptId}/split-comparison" as never,
        {
          params: { path: { receiptId } },
        } as never,
      );
      if (error) throw error;
      return data as unknown as ReceiptYnabSplitComparisonResponse;
    },
    enabled: !!receiptId && enabled,
    retry: false,
  });
}

export function usePushYnabTransactions() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async (receiptId: string) => {
      const { data, error } = await client.POST(
        "/api/ynab/push-transactions",
        { body: { receiptId } },
      );
      if (error) throw error;
      return data as unknown as PushYnabTransactionsResponse;
    },
    onSuccess: (data) => {
      queryClient.invalidateQueries({ queryKey: ["ynab", "sync-status"] });
      queryClient.invalidateQueries({ queryKey: ["ynab", "receipt-sync-statuses"] });
      queryClient.invalidateQueries({ queryKey: ["ynab", "split-comparison"] });
      if (data?.success) {
        toast.success(
          `Pushed ${data.pushedTransactions.length} transaction(s) to YNAB`,
        );
      } else {
        toast.error(data?.error ?? "Failed to push transactions to YNAB");
      }
    },
    onError: () => {
      toast.error("Failed to push transactions to YNAB");
    },
  });
}

export function useBulkPushYnabTransactions() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async (receiptIds: string[]) => {
      const { data, error } = await client.POST(
        "/api/ynab/push-transactions/bulk",
        { body: { receiptIds } },
      );
      if (error) throw error;
      return data as unknown as BulkPushYnabTransactionsResponse;
    },
    onSuccess: (data) => {
      queryClient.invalidateQueries({ queryKey: ["ynab", "sync-status"] });
      queryClient.invalidateQueries({ queryKey: ["ynab", "receipt-sync-statuses"] });
      const total = data?.results?.length ?? 0;
      const succeeded =
        data?.results?.filter((r) => r.result.success).length ?? 0;
      toast.success(`Pushed ${succeeded}/${total} receipt(s) to YNAB`);
    },
    onError: () => {
      toast.error("Failed to bulk push transactions to YNAB");
    },
  });
}

const ALL_RECEIPTS_PAGE_SIZE = 500;

export async function fetchAllReceiptIds(): Promise<{
  ids: string[];
  total: number;
}> {
  const ids: string[] = [];
  let offset = 0;
  let total = 0;

  // eslint-disable-next-line @typescript-eslint/no-unnecessary-condition
  while (true) {
    const { data, error } = await client.GET("/api/receipts", {
      params: { query: { offset, limit: ALL_RECEIPTS_PAGE_SIZE } },
    });
    if (error) throw error;
    total = Number(data?.total ?? 0);
    const page = data?.data ?? [];
    for (const r of page) {
      if (r.id) ids.push(r.id);
    }
    if (page.length < ALL_RECEIPTS_PAGE_SIZE || ids.length >= total) {
      break;
    }
    offset += ALL_RECEIPTS_PAGE_SIZE;
  }

  return { ids, total };
}

export function useAllReceiptIds(enabled = true) {
  const query = useQuery({
    queryKey: ["receipts", "all-ids"],
    queryFn: fetchAllReceiptIds,
    enabled,
  });
  return useMemo(
    () => ({
      ...query,
      receiptIds: query.data?.ids ?? [],
      totalReceipts: query.data?.total ?? 0,
      isTruncated: (query.data?.total ?? 0) > (query.data?.ids.length ?? 0),
    }),
    [query],
  );
}

export type ReceiptYnabSyncStatusValue =
  "NotSynced" | "Pending" | "Synced" | "Failed";
export type ReceiptYnabSyncStatus = {
  receiptId: string;
  syncStatus: ReceiptYnabSyncStatusValue;
};

type ReceiptYnabSyncStatusListResponse = {
  data: ReceiptYnabSyncStatus[];
};

export function useReceiptYnabSyncStatuses(receiptIds: string[]) {
  const query = useQuery({
    queryKey: ["ynab", "receipt-sync-statuses", receiptIds],
    queryFn: async (): Promise<ReceiptYnabSyncStatusListResponse> => {
      if (receiptIds.length === 0) return { data: [] };
      const res = await client.GET(
        "/api/ynab/receipt-sync-statuses" as "/api/ynab/budgets",
        {
          params: { query: { receiptIds } } as never,
        },
      );
      if (res.error) return { data: [] };
      return res.data as unknown as ReceiptYnabSyncStatusListResponse;
    },
    enabled: receiptIds.length > 0,
    retry: false,
    staleTime: 30_000,
  });
  return useMemo(() => {
    const statusMap = new Map<string, ReceiptYnabSyncStatusValue>();
    for (const item of query.data?.data ?? []) {
      statusMap.set(item.receiptId, item.syncStatus);
    }
    return { ...query, statusMap };
  }, [query]);
}

export function useYnabSyncStatus(transactionId: string | null) {
  return useQuery({
    queryKey: ["ynab", "sync-status", transactionId],
    queryFn: async () => {
      if (!transactionId) return null;
      const { data, error } = await client.GET(
        "/api/ynab/sync-status/{transactionId}",
        {
          params: {
            path: { transactionId },
            query: { syncType: "TransactionPush" as const },
          },
        } as never,
      );
      if (error) return null;
      return data;
    },
    enabled: !!transactionId,
  });
}

type YnabRateLimitStatusResponse = {
  remainingRequests: number;
  maxRequests: number;
  requestsUsed: number;
  windowResetAt?: null | string;
  oldestRequestAt?: null | string;
};

export function useYnabRateLimitStatus(enabled = true) {
  const query = useQuery({
    queryKey: ["ynab", "rate-limit-status"],
    queryFn: async () => {
      const { data, error } = await client.GET(
        "/api/ynab/rate-limit-status" as never,
        {} as never,
      );
      if (error) throw error;
      return data as unknown as YnabRateLimitStatusResponse;
    },
    enabled,
    refetchInterval: 30_000,
  });
  return useMemo(
    () => ({
      ...query,
      rateLimitStatus: query.data ?? null,
    }),
    [query],
  );
}
