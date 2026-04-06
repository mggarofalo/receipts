import { useMemo } from "react";
import { useQuery, useMutation, useQueryClient } from "@tanstack/react-query";
import client from "@/lib/api-client";
import { toast } from "sonner";
import type { components } from "@/generated/api";

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

export type YnabMemoSyncResult =
  components["schemas"]["YnabMemoSyncResultItem"];
export type YnabMemoSyncOutcome =
  components["schemas"]["YnabMemoSyncOutcome"];
export type YnabTransactionCandidateDto =
  components["schemas"]["YnabTransactionCandidate"];

export function useSyncYnabMemos() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async (receiptId: string) => {
      const { data, error } = await client.POST("/api/ynab/sync-memos", {
        body: { receiptId },
      });
      if (error) throw error;
      return data;
    },
    onSuccess: (data) => {
      queryClient.invalidateQueries({ queryKey: ["ynab", "sync-status"] });
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
      return data;
    },
    onSuccess: (data) => {
      queryClient.invalidateQueries({ queryKey: ["ynab", "sync-status"] });
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
      failed: results.filter((r) => r.outcome === "Failed").length,
      total: results.length,
    };
  }, [results]);
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
      return data;
    },
    onSuccess: (data) => {
      queryClient.invalidateQueries({ queryKey: ["ynab", "sync-status"] });
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
      return data;
    },
    onSuccess: (data) => {
      queryClient.invalidateQueries({ queryKey: ["ynab", "sync-status"] });
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

export function useAllReceiptIds(enabled = true) {
  const query = useQuery({
    queryKey: ["receipts", "all-ids"],
    queryFn: async () => {
      const { data, error } = await client.GET("/api/receipts", {
        params: { query: { offset: 0, limit: 10000 } },
      });
      if (error) throw error;
      return data;
    },
    enabled,
  });
  return useMemo(
    () => ({
      ...query,
      receiptIds: query.data?.data?.map((r) => r.id).filter(Boolean) as string[] ?? [],
      totalReceipts: query.data?.total ?? 0,
    }),
    [query],
  );
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
            query: { syncType: "TransactionPush" },
          },
        },
      );
      if (error) return null;
      return data;
    },
    enabled: !!transactionId,
  });
}
