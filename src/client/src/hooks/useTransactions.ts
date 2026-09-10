import { toastErrorPolicy } from "@/lib/request-error-policy";
import { invalidateDomainChange, queryKeys, repairDomainChange } from "@/lib/query-invalidation";
import { useMemo } from "react";
import { useStableQuery } from "@/hooks/useStableQuery";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useSessionMutation } from "@/hooks/useSessionMutation";
import client from "@/lib/api-client";
import type { components } from "@/generated/api";
import { toast } from "sonner";
import { getSessionVersion } from "@/lib/auth";

function repairYnabSyncRecordChange(queryClient: ReturnType<typeof useQueryClient>) {
  const sessionVersion = getSessionVersion();
  return repairDomainChange(
    queryClient,
    "ynab-sync-record",
    () => getSessionVersion() === sessionVersion,
  );
}

export function useTransactions(offset = 0, limit = 50, sortBy?: string | null, sortDirection?: string | null) {
  const query = useQuery({
    queryKey: [...queryKeys.transactions, "list", offset, limit, sortBy, sortDirection],
    queryFn: async () => {
      const { data, error } = await client.GET("/api/transactions", {
        params: { query: { offset, limit, sortBy: sortBy ?? undefined, sortDirection: (sortDirection ?? undefined) as "asc" | "desc" | undefined } },
      });
      if (error) throw error;
      return data;
    },
  });
  const base = useStableQuery(query);
  return useMemo(() => ({ ...base, data: query.data?.data, total: query.data?.total ?? 0 }), [base, query.data]);
}

export function useTransaction(id: string | null) {
  return useQuery({
    queryKey: [...queryKeys.transactions, id],
    enabled: !!id,
    queryFn: async () => {
      const { data, error } = await client.GET("/api/transactions/{id}", {
        params: { path: { id: id! } },
      });
      if (error) throw error;
      return data;
    },
  });
}

export function useTransactionsByReceiptId(receiptId: string | null, offset = 0, limit = 200, sortBy?: string | null, sortDirection?: string | null) {
  const query = useQuery({
    queryKey: [...queryKeys.transactions, "by-receipt", receiptId, offset, limit, sortBy, sortDirection],
    enabled: !!receiptId,
    queryFn: async () => {
      const { data, error } = await client.GET("/api/transactions", {
        params: { query: { receiptId: receiptId!, offset, limit, sortBy: sortBy ?? undefined, sortDirection: (sortDirection ?? undefined) as "asc" | "desc" | undefined } },
      });
      if (error) throw error;
      return data;
    },
  });
  const base = useStableQuery(query);
  return useMemo(() => ({ ...base, data: query.data?.data, total: query.data?.total ?? 0 }), [base, query.data]);
}

export function useCreateTransaction() {
  const queryClient = useQueryClient();
  return useSessionMutation({
    ...toastErrorPolicy.mutation,
    mutationFn: async ({
      receiptId,
      body,
    }: {
      receiptId: string;
      body: components["schemas"]["CreateTransactionRequest"];
    }) => {
      const { data, error } = await client.POST(
        "/api/receipts/{receiptId}/transactions",
        { ...toastErrorPolicy.request, params: { path: { receiptId } }, body: { cardId: body.cardId, amount: body.amount, date: body.date } },
      );
      if (error) throw error;
      return data;
    },
    onSuccess: () => {
      invalidateDomainChange(queryClient, "transaction");
      toast.success("Transaction created");
    },
  });
}

export function useCreateTransactionsBatch() {
  const queryClient = useQueryClient();
  return useSessionMutation({
    ...toastErrorPolicy.mutation,
    mutationFn: async ({
      receiptId,
      body,
    }: {
      receiptId: string;
      body: components["schemas"]["CreateTransactionRequest"][];
    }) => {
      const { data, error } = await client.POST(
        "/api/receipts/{receiptId}/transactions/batch",
        { ...toastErrorPolicy.request, params: { path: { receiptId } }, body: body.map(({ cardId, amount, date }) => ({ cardId, amount, date })) },
      );
      if (error) throw error;
      return data;
    },
    onSuccess: () => {
      invalidateDomainChange(queryClient, "transaction");
    },
  });
}

export function useUpdateTransaction() {
  const queryClient = useQueryClient();
  return useSessionMutation({
    ...toastErrorPolicy.mutation,
    mutationFn: async ({
      body,
    }: {
      body: components["schemas"]["UpdateTransactionRequest"];
    }) => {
      const { error } = await client.PUT("/api/transactions/{id}", {
        ...toastErrorPolicy.request,
        params: { path: { id: body.id } },
        body: { id: body.id, cardId: body.cardId, amount: body.amount, date: body.date },
      });
      if (error) throw error;
    },
    onSuccess: () => {
      invalidateDomainChange(queryClient, "transaction");
      toast.success("Transaction updated");
    },
  });
}

export function useDeleteTransactions() {
  const queryClient = useQueryClient();
  return useSessionMutation({
    ...toastErrorPolicy.mutation,
    mutationFn: async (ids: string[]) => {
      const { error } = await client.DELETE("/api/transactions", {
        ...toastErrorPolicy.request,
        body: ids,
      });
      if (error) throw error;
    },
    onMutate: async (ids) => {
      await queryClient.cancelQueries({ queryKey: [...queryKeys.transactions] });
      const previous = queryClient.getQueriesData<{ data: { id: string }[]; total: number }>({ queryKey: [...queryKeys.transactions, "list"] });
      for (const [key] of previous) {
        queryClient.setQueryData(key, (old: { data: { id: string }[]; total: number; offset: number; limit: number } | undefined) => {
          if (!old?.data) return old;
          const filtered = old.data.filter((item) => !ids.includes(item.id));
          return { ...old, data: filtered, total: old.total - (old.data.length - filtered.length) };
        });
      }
      return { previous };
    },
    onError: (_err, _ids, context) => {
      for (const [key, data] of context?.previous ?? []) {
        queryClient.setQueryData(key, data);
      }
    },
    onSettled: () => {
      invalidateDomainChange(queryClient, "transaction");
    },
    onSuccess: () => {
      toast.success("Transaction(s) deleted");
      return repairYnabSyncRecordChange(queryClient);
    },
  });
}

export function useDeletedTransactions(offset = 0, limit = 50, sortBy?: string | null, sortDirection?: string | null) {
  const query = useQuery({
    queryKey: [...queryKeys.transactions, "deleted", offset, limit, sortBy, sortDirection],
    queryFn: async () => {
      const { data, error } = await client.GET("/api/transactions/deleted", {
        params: { query: { offset, limit, sortBy: sortBy ?? undefined, sortDirection: (sortDirection ?? undefined) as "asc" | "desc" | undefined } },
      });
      if (error) throw error;
      return data;
    },
  });
  const base = useStableQuery(query);
  return useMemo(() => ({ ...base, data: query.data?.data, total: query.data?.total ?? 0 }), [base, query.data]);
}

export function useRestoreTransaction() {
  const queryClient = useQueryClient();
  return useSessionMutation({
    ...toastErrorPolicy.mutation,
    mutationFn: async (id: string) => {
      const { error } = await client.POST("/api/transactions/{id}/restore", {
        ...toastErrorPolicy.request,
        params: { path: { id } },
      });
      if (error) throw error;
    },
    onSuccess: () => {
      invalidateDomainChange(queryClient, "transaction");
      toast.success("Transaction restored");
      return repairYnabSyncRecordChange(queryClient);
    },
  });
}
