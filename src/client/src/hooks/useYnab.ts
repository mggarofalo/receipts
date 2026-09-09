import { useCallback, useMemo, useState } from "react";
import { useStableQuery } from "@/hooks/useStableQuery";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useSessionMutation } from "@/hooks/useSessionMutation";
import { localErrorPolicy, toastErrorPolicy } from "@/lib/request-error-policy";
import { parseProblemDetails } from "@/lib/problem-details";
import { assertSessionCurrent, getSessionVersion } from "@/lib/auth";
import client from "@/lib/api-client";
import type { components } from "@/generated/api";
import { toast } from "sonner";
import {
  repairDomainChanges,
  type DomainChange,
} from "@/lib/query-invalidation";
// A few unrelated schemas still use narrow local projections where the
// generated graph exceeds TypeScript's type-resolution depth.

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

const budgetDependentYnabQueryKeys = [
  ["ynab", "accounts"],
  ["ynab", "account-mappings"],
  ["ynab", "categories"],
  ["ynab", "category-mappings"],
  ["ynab", "stale-mappings"],
  ["ynab", "split-comparison"],
  ["ynab", "receipt-sync-statuses"],
  ["ynab", "sync-status"],
] as const;

const ynabBudgetChanges = ["ynab-budget"] as const satisfies readonly DomainChange[];
const ynabMappingChanges = ["ynab-mapping"] as const satisfies readonly DomainChange[];
const ynabSyncChanges = [
  "ynab-sync-record",
  "ynab-sync-event",
] as const satisfies readonly DomainChange[];
const ynabSyncEventChanges = [
  "ynab-sync-event",
] as const satisfies readonly DomainChange[];

function useYnabSettlementRepair(changes: readonly DomainChange[]) {
  const queryClient = useQueryClient();
  const [sessionVersion] = useState(getSessionVersion);
  return useCallback(
    () =>
      repairDomainChanges(
        queryClient,
        changes,
        () => getSessionVersion() === sessionVersion,
      ),
    [changes, queryClient, sessionVersion],
  );
}

export function useYnabConnectionStatus() {
  const repairAfterValidation = useYnabSettlementRepair(ynabSyncEventChanges);
  const query = useQuery({
    ...localErrorPolicy.query,
    queryKey: ["ynab", "connection-status"],
    staleTime: 5 * 60 * 1000, // 5 min — matches backend budget cache TTL
    queryFn: async ({ signal }) => {
      try {
        const { data, error } = await client.GET(
          "/api/ynab/connection-status" as never,
          { ...localErrorPolicy.request, signal } as never,
        );
        if (error) throw error;
        return data as unknown as YnabConnectionStatusResponse;
      } finally {
        // This read appends a validation event before its final status lookup.
        // Repair its projections even when SignalR is unavailable or that later
        // lookup makes the request fail after the event commit.
        await repairAfterValidation();
      }
    },
  });
  const base = useStableQuery(query);
  return useMemo(
    () => ({
      ...base,
      isConfigured: query.data?.isConfigured ?? false,
      isConnected: query.data?.isConnected ?? false,
      lastSuccessfulSyncUtc: query.data?.lastSuccessfulSyncUtc ?? null,
    }),
    [base, query.data],
  );
}

export function useYnabBudgets() {
  const query = useQuery({
    ...localErrorPolicy.query,
    queryKey: ["ynab", "budgets"],
    queryFn: async ({ signal }) => {
      const { data, error } = await client.GET("/api/ynab/budgets", {
        ...localErrorPolicy.request,
        signal,
      });
      if (error) throw error;
      return data;
    },
  });
  const base = useStableQuery(query);
  return useMemo(
    () => ({ ...base, budgets: query.data?.data ?? [] }),
    [base, query.data],
  );
}

export function useSelectedYnabBudget() {
  const query = useQuery({
    ...localErrorPolicy.query,
    queryKey: ["ynab", "settings", "budget"],
    queryFn: async ({ signal }) => {
      const { data, error } = await client.GET("/api/ynab/settings/budget", {
        ...localErrorPolicy.request,
        signal,
      });
      if (error) throw error;
      return data;
    },
  });
  const base = useStableQuery(query);
  return useMemo(
    () => ({
      ...base,
      selectedBudgetId: query.data?.selectedBudgetId ?? null,
    }),
    [base, query.data],
  );
}

export function useSelectYnabBudget() {
  const queryClient = useQueryClient();
  const [sessionVersion] = useState(getSessionVersion);
  const repairOnSettled = useYnabSettlementRepair(ynabBudgetChanges);
  return useSessionMutation({
    ...toastErrorPolicy.mutation,
    onMutate: async () => {
      await Promise.all(
        budgetDependentYnabQueryKeys.map((queryKey) =>
          queryClient.cancelQueries({ queryKey }),
        ),
      );
    },
    mutationFn: async (budgetId: string) => {
      const { error } = await client.PUT("/api/ynab/settings/budget", {
        ...toastErrorPolicy.request,
        body: { budgetId },
      });
      if (error) throw error;
    },
    onSuccess: (_, budgetId) => {
      queryClient.setQueryData(["ynab", "settings", "budget"], {
        selectedBudgetId: budgetId,
      });
      assertSessionCurrent(sessionVersion);
      toast.success("YNAB budget selected");
    },
    onSettled: repairOnSettled,
  });
}

export function useYnabAccounts(enabled = true, budgetId?: string | null) {
  const query = useQuery({
    ...localErrorPolicy.query,
    queryKey: budgetId ? ["ynab", "accounts", budgetId] : ["ynab", "accounts"],
    queryFn: async ({ signal }) => {
      const { data, error } = await client.GET("/api/ynab/accounts", {
        ...localErrorPolicy.request,
        signal,
      });
      if (error) throw error;
      return data;
    },
    enabled,
  });
  const base = useStableQuery(query);
  return useMemo(
    () => ({ ...base, accounts: query.data?.data ?? [] }),
    [base, query.data],
  );
}

export function useYnabAccountMappings(
  enabled = true,
  budgetId?: string | null,
) {
  const query = useQuery({
    ...localErrorPolicy.query,
    queryKey: budgetId
      ? ["ynab", "account-mappings", budgetId]
      : ["ynab", "account-mappings"],
    queryFn: async ({ signal }) => {
      const { data, error } = await client.GET("/api/ynab/account-mappings", {
        ...localErrorPolicy.request,
        signal,
      });
      if (error) throw error;
      return data;
    },
    enabled,
  });
  const base = useStableQuery(query);
  return useMemo(
    () => ({ ...base, mappings: query.data?.data ?? [] }),
    [base, query.data],
  );
}

export function useCreateYnabAccountMapping() {
  const repairOnSettled = useYnabSettlementRepair(ynabMappingChanges);
  return useSessionMutation({
    ...toastErrorPolicy.mutation,
    mutationFn: async (body: {
      receiptsAccountId: string;
      ynabAccountId: string;
      ynabAccountName: string;
      ynabBudgetId: string;
    }) => {
      const { data, error } = await client.POST("/api/ynab/account-mappings", {
        ...toastErrorPolicy.request,
        body,
      });
      if (error) throw error;
      return data;
    },
    onSuccess: () => {
      toast.success("Account mapping created");
    },
    onSettled: repairOnSettled,
  });
}

export function useUpdateYnabAccountMapping() {
  const repairOnSettled = useYnabSettlementRepair(ynabMappingChanges);
  return useSessionMutation({
    ...toastErrorPolicy.mutation,
    mutationFn: async (params: {
      id: string;
      ynabAccountId: string;
      ynabAccountName: string;
      ynabBudgetId: string;
    }) => {
      const { error } = await client.PUT("/api/ynab/account-mappings/{id}", {
        ...toastErrorPolicy.request,
        params: { path: { id: params.id } },
        body: {
          ynabAccountId: params.ynabAccountId,
          ynabAccountName: params.ynabAccountName,
          ynabBudgetId: params.ynabBudgetId,
        },
      });
      if (error) throw error;
    },
    onSuccess: () => {
      toast.success("Account mapping updated");
    },
    onSettled: repairOnSettled,
  });
}

export function useDeleteYnabAccountMapping() {
  const repairOnSettled = useYnabSettlementRepair(ynabMappingChanges);
  return useSessionMutation({
    ...toastErrorPolicy.mutation,
    mutationFn: async (id: string) => {
      const { error } = await client.DELETE("/api/ynab/account-mappings/{id}", {
        ...toastErrorPolicy.request,
        params: { path: { id } },
      });
      if (error) throw error;
    },
    onSuccess: () => {
      toast.success("Account mapping removed");
    },
    onSettled: repairOnSettled,
  });
}

export function useYnabCategories(enabled = true, budgetId?: string | null) {
  const query = useQuery({
    ...localErrorPolicy.query,
    queryKey: budgetId
      ? ["ynab", "categories", budgetId]
      : ["ynab", "categories"],
    queryFn: async ({ signal }) => {
      const { data, error } = await client.GET("/api/ynab/categories", {
        ...localErrorPolicy.request,
        signal,
      });
      if (error) throw error;
      return data;
    },
    enabled,
  });
  const base = useStableQuery(query);
  return useMemo(
    () => ({ ...base, categories: query.data?.data ?? [] }),
    [base, query.data],
  );
}

export function useDistinctReceiptItemCategories(enabled = true) {
  const query = useQuery({
    ...localErrorPolicy.query,
    queryKey: ["receipt-items", "distinct-categories"],
    queryFn: async ({ signal }) => {
      const { data, error } = await client.GET(
        "/api/receipt-items/distinct-categories",
        { ...localErrorPolicy.request, signal },
      );
      if (error) throw error;
      return data;
    },
    enabled,
  });
  const base = useStableQuery(query);
  return useMemo(
    () => ({ ...base, categories: query.data?.categories ?? [] }),
    [base, query.data],
  );
}

export function useYnabCategoryMappings(
  enabled = true,
  budgetId?: string | null,
) {
  const query = useQuery({
    ...localErrorPolicy.query,
    queryKey: budgetId
      ? ["ynab", "category-mappings", budgetId]
      : ["ynab", "category-mappings"],
    queryFn: async ({ signal }) => {
      const { data, error } = await client.GET("/api/ynab/category-mappings", {
        ...localErrorPolicy.request,
        signal,
      });
      if (error) throw error;
      return data;
    },
    enabled,
  });
  const base = useStableQuery(query);
  return useMemo(
    () => ({ ...base, mappings: query.data?.data ?? [] }),
    [base, query.data],
  );
}

export function useUnmappedCategories(
  enabled = true,
  budgetId?: string | null,
) {
  const query = useQuery({
    ...localErrorPolicy.query,
    queryKey: budgetId
      ? ["ynab", "category-mappings", "unmapped", budgetId]
      : ["ynab", "category-mappings", "unmapped"],
    queryFn: async ({ signal }) => {
      const { data, error } = await client.GET(
        "/api/ynab/category-mappings/unmapped",
        { ...localErrorPolicy.request, signal },
      );
      if (error) throw error;
      return data;
    },
    enabled,
  });
  const base = useStableQuery(query);
  return useMemo(
    () => ({
      ...base,
      unmappedCategories: query.data?.unmappedCategories ?? [],
    }),
    [base, query.data],
  );
}

export function useCreateYnabCategoryMapping() {
  const repairOnSettled = useYnabSettlementRepair(ynabMappingChanges);
  return useSessionMutation({
    ...toastErrorPolicy.mutation,
    mutationFn: async (body: {
      receiptsCategory: string;
      ynabCategoryId: string;
      ynabCategoryName: string;
      ynabCategoryGroupName: string;
      ynabBudgetId: string;
    }) => {
      const { data, error } = await client.POST("/api/ynab/category-mappings", {
        ...toastErrorPolicy.request,
        body,
      });
      if (error) throw error;
      return data;
    },
    onSuccess: () => {
      toast.success("Category mapping created");
    },
    onSettled: repairOnSettled,
  });
}

export function useUpdateYnabCategoryMapping() {
  const repairOnSettled = useYnabSettlementRepair(ynabMappingChanges);
  return useSessionMutation({
    ...toastErrorPolicy.mutation,
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
      const { error } = await client.PUT("/api/ynab/category-mappings/{id}", {
        ...toastErrorPolicy.request,
        params: { path: { id } },
        body,
      });
      if (error) throw error;
    },
    onSuccess: () => {
      toast.success("Category mapping updated");
    },
    onSettled: repairOnSettled,
  });
}

export function useStaleMappings(enabled = true, budgetId?: string | null) {
  const query = useQuery({
    ...localErrorPolicy.query,
    queryKey: budgetId
      ? ["ynab", "stale-mappings", budgetId]
      : ["ynab", "stale-mappings"],
    queryFn: async ({ signal }) => {
      const { data, error } = await client.GET(
        "/api/ynab/stale-mappings" as never,
        { ...localErrorPolicy.request, signal } as never,
      );
      if (error) throw error;
      return data as unknown as StaleMappingsResponse;
    },
    enabled,
  });
  const base = useStableQuery(query);
  return useMemo(
    () => ({
      ...base,
      staleAccountMappingCount: query.data?.staleAccountMappingCount ?? 0,
      staleCategoryMappingCount: query.data?.staleCategoryMappingCount ?? 0,
      hasStaleMappings:
        (query.data?.staleAccountMappingCount ?? 0) > 0 ||
        (query.data?.staleCategoryMappingCount ?? 0) > 0,
    }),
    [base, query.data],
  );
}

type ClearStaleMappingsResponse = {
  deletedAccountMappings: number;
  deletedCategoryMappings: number;
};

export function useClearStaleMappings() {
  const repairOnSettled = useYnabSettlementRepair(ynabMappingChanges);
  return useSessionMutation({
    ...toastErrorPolicy.mutation,
    mutationFn: async () => {
      const { data, error } = await client.DELETE(
        "/api/ynab/stale-mappings" as never,
        toastErrorPolicy.request as never,
      );
      if (error) throw error;
      return data as unknown as ClearStaleMappingsResponse;
    },
    onSuccess: (data) => {
      const total =
        (data?.deletedAccountMappings ?? 0) +
        (data?.deletedCategoryMappings ?? 0);
      toast.success(`Cleared ${total} stale mapping(s)`);
    },
    onSettled: repairOnSettled,
  });
}

export function useDeleteYnabCategoryMapping() {
  const repairOnSettled = useYnabSettlementRepair(ynabMappingChanges);
  return useSessionMutation({
    ...toastErrorPolicy.mutation,
    mutationFn: async (id: string) => {
      const { error } = await client.DELETE(
        "/api/ynab/category-mappings/{id}",
        { ...toastErrorPolicy.request, params: { path: { id } } },
      );
      if (error) throw error;
    },
    onSuccess: () => {
      toast.success("Category mapping deleted");
    },
    onSettled: repairOnSettled,
  });
}

export type YnabMemoSyncOutcome = components["schemas"]["YnabMemoSyncOutcome"];
export type YnabTransactionCandidateDto =
  components["schemas"]["YnabTransactionCandidate"];
export type YnabMemoSyncResult =
  components["schemas"]["YnabMemoSyncResultItem"];

export function isCompletedYnabMemoResolution(
  result: YnabMemoSyncResult | null | undefined,
): boolean {
  return result?.outcome === "synced" || result?.outcome === "alreadySynced";
}

function showMemoSyncResultsToast(results: YnabMemoSyncResult[]): void {
  const syncedCount = results.filter(
    (result) => result.outcome === "synced",
  ).length;
  const failedCount = results.filter(
    (result) => result.outcome === "failed",
  ).length;

  if (syncedCount > 0 && failedCount > 0) {
    toast.warning(
      `Synced ${syncedCount} transaction memo(s) to YNAB; ${failedCount} failed`,
    );
  } else if (failedCount > 0) {
    toast.error(`Failed to sync ${failedCount} transaction memo(s) to YNAB`);
  } else if (syncedCount > 0) {
    toast.success(`Synced ${syncedCount} transaction memo(s) to YNAB`);
  } else {
    toast.info("No transactions were synced");
  }
}
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
  const repairOnSettled = useYnabSettlementRepair(ynabSyncChanges);
  return useSessionMutation({
    ...localErrorPolicy.mutation,
    mutationFn: async (receiptId: string) => {
      const { data, error } = await client.POST("/api/ynab/sync-memos", {
        ...localErrorPolicy.request,
        body: { receiptId },
      });
      if (error) throw error;
      return data;
    },
    onSuccess: (data) => {
      showMemoSyncResultsToast(data?.results ?? []);
    },
    onSettled: repairOnSettled,
  });
}

export function useSyncYnabMemosBulk() {
  const repairOnSettled = useYnabSettlementRepair(ynabSyncChanges);
  return useSessionMutation({
    ...localErrorPolicy.mutation,
    mutationFn: async (receiptIds: string[]) => {
      const { data, error } = await client.POST("/api/ynab/sync-memos/bulk", {
        ...localErrorPolicy.request,
        body: { receiptIds },
      });
      if (error) throw error;
      return data;
    },
    onSuccess: (data) => {
      showMemoSyncResultsToast(data?.results ?? []);
    },
    onSettled: repairOnSettled,
  });
}

export function useResolveYnabMemoSync() {
  const repairOnSettled = useYnabSettlementRepair(ynabSyncChanges);
  return useSessionMutation({
    ...localErrorPolicy.mutation,
    mutationFn: async (params: {
      localTransactionId: string;
      ynabTransactionId: string;
    }) => {
      const { data, error } = await client.POST(
        "/api/ynab/sync-memos/resolve",
        { ...localErrorPolicy.request, body: params },
      );
      if (error) throw error;
      return data;
    },
    onSuccess: (data) => {
      if (data?.outcome === "synced") {
        toast.success("YNAB memo sync resolved");
      } else if (data?.outcome === "alreadySynced") {
        toast.info("YNAB memo was already synced");
      } else if (data?.outcome === "failed") {
        toast.error(data.error ?? "Failed to resolve YNAB memo sync");
      } else if (data?.outcome === "reconciledSkipped") {
        toast.warning(
          data.error ??
            "The YNAB transaction is reconciled and was not changed",
        );
      } else if (data?.outcome === "currencySkipped") {
        toast.warning(
          data.error ??
            "The transaction currency is not supported for memo sync",
        );
      } else {
        toast.warning(data?.error ?? "YNAB memo sync was not resolved");
      }
    },
    onSettled: repairOnSettled,
  });
}

export function useMemoSyncSummary(results: YnabMemoSyncResult[] | undefined) {
  return useMemo(() => {
    if (!results) return null;
    return {
      synced: results.filter((r) => r.outcome === "synced").length,
      alreadySynced: results.filter((r) => r.outcome === "alreadySynced")
        .length,
      noMatch: results.filter((r) => r.outcome === "noMatch").length,
      ambiguous: results.filter((r) => r.outcome === "ambiguous").length,
      currencySkipped: results.filter((r) => r.outcome === "currencySkipped")
        .length,
      reconciledSkipped: results.filter(
        (r) => r.outcome === "reconciledSkipped",
      ).length,
      failed: results.filter((r) => r.outcome === "failed").length,
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
    ...localErrorPolicy.query,
    queryKey: ["ynab", "split-comparison", receiptId],
    queryFn: async (): Promise<ReceiptYnabSplitComparisonResponse> => {
      const { data, error } = await client.GET(
        "/api/ynab/receipts/{receiptId}/split-comparison" as never,
        {
          ...localErrorPolicy.request,
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
  const repairOnSettled = useYnabSettlementRepair(ynabSyncChanges);
  return useSessionMutation({
    ...localErrorPolicy.mutation,
    mutationFn: async (receiptId: string) => {
      const { data, error } = await client.POST("/api/ynab/push-transactions", {
        ...localErrorPolicy.request,
        body: { receiptId },
      });
      if (error) throw error;
      return data as unknown as PushYnabTransactionsResponse;
    },
    onSuccess: (data) => {
      if (data?.success) {
        toast.success(
          `Pushed ${data.pushedTransactions.length} transaction(s) to YNAB`,
        );
      } else {
        toast.error(data?.error ?? "Failed to push transactions to YNAB");
      }
    },
    onSettled: repairOnSettled,
  });
}

export function useBulkPushYnabTransactions() {
  const repairOnSettled = useYnabSettlementRepair(ynabSyncChanges);
  return useSessionMutation({
    ...localErrorPolicy.mutation,
    mutationFn: async (receiptIds: string[]) => {
      const { data, error } = await client.POST(
        "/api/ynab/push-transactions/bulk",
        { ...localErrorPolicy.request, body: { receiptIds } },
      );
      if (error) throw error;
      return data as unknown as BulkPushYnabTransactionsResponse;
    },
    onSuccess: (data) => {
      const results = data?.results ?? [];
      const total = results.length;
      if (total === 0) return;

      const succeeded = results.filter((r) => r.result.success).length;
      const failed = total - succeeded;

      // Aggregate the distinct unmapped categories the server reported across
      // the failed receipts so the user knows exactly what to map (the same
      // detail the single-receipt push button surfaces). RECEIPTS-783.
      const unmapped = Array.from(
        new Set(results.flatMap((r) => r.result.unmappedCategories ?? [])),
      );
      const unmappedSuffix = unmapped.length
        ? ` Unmapped categories: ${unmapped.join(", ")}. Map them in YNAB Settings.`
        : "";

      if (succeeded === 0) {
        // Every receipt failed — this is an error, not a success.
        toast.error(
          `Failed to push ${failed} receipt(s) to YNAB.${unmappedSuffix}`,
        );
      } else if (failed > 0) {
        // Partial success — warn and name what needs attention.
        toast.warning(
          `Pushed ${succeeded}/${total} receipt(s); ${failed} failed.${unmappedSuffix}`,
        );
      } else {
        toast.success(`Pushed ${succeeded}/${total} receipt(s) to YNAB`);
      }
    },
    onSettled: repairOnSettled,
  });
}

const ALL_RECEIPTS_PAGE_SIZE = 500;

export async function fetchAllReceiptIds(signal?: AbortSignal): Promise<{
  ids: string[];
  total: number;
}> {
  const ids = new Set<string>();
  let offset = 0;
  let rowsRead = 0;
  let expectedTotal: number | undefined;

  while (true) {
    const { data, error } = await client.GET("/api/receipts", {
      ...localErrorPolicy.request,
      signal,
      params: { query: { offset, limit: ALL_RECEIPTS_PAGE_SIZE } },
    });
    if (error) throw error;
    const pageTotal = Number(data?.total ?? 0);
    if (expectedTotal !== undefined && pageTotal !== expectedTotal) {
      throw new Error("The receipt list changed while it was being loaded");
    }
    expectedTotal = pageTotal;
    const page = data?.data ?? [];
    for (const r of page) {
      if (r.id) ids.add(r.id);
    }
    rowsRead += page.length;
    if (page.length < ALL_RECEIPTS_PAGE_SIZE || rowsRead >= expectedTotal) {
      break;
    }
    offset = rowsRead;
  }

  return { ids: Array.from(ids), total: expectedTotal ?? 0 };
}

export function useAllReceiptIds(enabled = true) {
  const query = useQuery({
    ...localErrorPolicy.query,
    queryKey: ["receipts", "all-ids"],
    queryFn: ({ signal }) => fetchAllReceiptIds(signal),
    enabled,
  });
  const base = useStableQuery(query);
  return useMemo(
    () => ({
      ...base,
      receiptIds: query.data?.ids ?? [],
      totalReceipts: query.data?.total ?? 0,
      isTruncated:
        query.data !== undefined && query.data.total !== query.data.ids.length,
    }),
    [base, query.data],
  );
}

export type ReceiptYnabSyncStatusValue =
  components["schemas"]["ReceiptYnabSyncStatusValue"];
export type ReceiptYnabSyncStatus =
  components["schemas"]["ReceiptYnabSyncStatus"];
type ReceiptYnabSyncStatusListResponse =
  components["schemas"]["ReceiptYnabSyncStatusListResponse"];

export function useReceiptYnabSyncStatuses(
  receiptIds: string[],
  enabled = true,
) {
  const query = useQuery({
    ...localErrorPolicy.query,
    queryKey: ["ynab", "receipt-sync-statuses", receiptIds],
    queryFn: async (): Promise<ReceiptYnabSyncStatusListResponse> => {
      if (receiptIds.length === 0) return { data: [] };
      const res = await client.GET("/api/ynab/receipt-sync-statuses", {
        ...localErrorPolicy.request,
        params: { query: { receiptIds } },
      });
      if ("error" in res) throw res.error;
      return res.data;
    },
    enabled: enabled && receiptIds.length > 0,
    retry: false,
    staleTime: 30_000,
  });
  const base = useStableQuery(query);
  return useMemo(() => {
    const statusMap = new Map<string, ReceiptYnabSyncStatusValue>();
    for (const item of query.data?.data ?? []) {
      statusMap.set(item.receiptId, item.syncStatus);
    }
    return { ...base, statusMap };
  }, [base, query.data]);
}

export function useYnabSyncStatus(transactionId: string | null) {
  return useQuery({
    ...localErrorPolicy.query,
    queryKey: ["ynab", "sync-status", transactionId],
    queryFn: async () => {
      if (!transactionId) return null;
      const { data, error } = await client.GET(
        "/api/ynab/sync-status/{transactionId}",
        {
          ...localErrorPolicy.request,
          params: {
            path: { transactionId },
            query: { syncType: "transactionPush" },
          },
        },
      );
      if (parseProblemDetails(error)?.status === 404) return null;
      if (error) throw error;
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
    ...localErrorPolicy.query,
    queryKey: ["ynab", "rate-limit-status"],
    queryFn: async ({ signal }) => {
      const { data, error } = await client.GET(
        "/api/ynab/rate-limit-status" as never,
        { ...localErrorPolicy.request, signal } as never,
      );
      if (error) throw error;
      return data as unknown as YnabRateLimitStatusResponse;
    },
    enabled,
    refetchInterval: 30_000,
  });
  const base = useStableQuery(query);
  return useMemo(
    () => ({
      ...base,
      rateLimitStatus: query.data ?? null,
    }),
    [base, query.data],
  );
}
