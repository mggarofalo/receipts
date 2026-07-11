import { useMemo } from "react";
import { useStableQuery } from "@/hooks/useStableQuery";
import { useQuery, useMutation, useQueryClient } from "@tanstack/react-query";
import client from "@/lib/api-client";
import { toast } from "sonner";

export function useTransactions(offset = 0, limit = 50, sortBy?: string | null, sortDirection?: string | null) {
  const query = useQuery({
    queryKey: ["transactions", "list", offset, limit, sortBy, sortDirection],
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
    queryKey: ["transactions", id],
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
    queryKey: ["transactions", "by-receipt", receiptId, offset, limit, sortBy, sortDirection],
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
  return useMutation({
    mutationFn: async ({
      receiptId,
      body,
    }: {
      receiptId: string;
      body: { amount: number; date: string; accountId: string; cardId: string };
    }) => {
      const { data, error } = await client.POST(
        "/api/receipts/{receiptId}/transactions",
        { params: { path: { receiptId } }, body },
      );
      if (error) throw error;
      return data;
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["transactions"] });
      queryClient.invalidateQueries({ queryKey: ["trips"] });
      toast.success("Transaction created");
    },
  });
}

export function useCreateTransactionsBatch() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async ({
      receiptId,
      body,
    }: {
      receiptId: string;
      body: { amount: number; date: string; accountId: string; cardId: string }[];
    }) => {
      const { data, error } = await client.POST(
        "/api/receipts/{receiptId}/transactions/batch",
        { params: { path: { receiptId } }, body },
      );
      if (error) throw error;
      return data;
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["transactions"] });
    },
  });
}

export function useUpdateTransaction() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async ({
      body,
    }: {
      body: { id: string; amount: number; date: string; accountId: string; cardId: string };
    }) => {
      const { error } = await client.PUT("/api/transactions/{id}", {
        params: { path: { id: body.id } },
        body,
      });
      if (error) throw error;
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["transactions"] });
      queryClient.invalidateQueries({ queryKey: ["trips"] });
      toast.success("Transaction updated");
    },
  });
}

export function useDeleteTransactions() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async (ids: string[]) => {
      const { error } = await client.DELETE("/api/transactions", {
        body: ids,
      });
      if (error) throw error;
    },
    onMutate: async (ids) => {
      await queryClient.cancelQueries({ queryKey: ["transactions"] });
      const previous = queryClient.getQueriesData<{ data: { id: string }[]; total: number }>({ queryKey: ["transactions", "list"] });
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
      queryClient.invalidateQueries({ queryKey: ["transactions"] });
      queryClient.invalidateQueries({ queryKey: ["transactions", "deleted"] });
      queryClient.invalidateQueries({ queryKey: ["trips"] });
    },
    onSuccess: () => {
      toast.success("Transaction(s) deleted");
    },
  });
}

export function useDeletedTransactions(offset = 0, limit = 50, sortBy?: string | null, sortDirection?: string | null) {
  const query = useQuery({
    queryKey: ["transactions", "deleted", offset, limit, sortBy, sortDirection],
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
  return useMutation({
    mutationFn: async (id: string) => {
      const { error } = await client.POST("/api/transactions/{id}/restore", {
        params: { path: { id } },
      });
      if (error) throw error;
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["transactions"] });
      queryClient.invalidateQueries({ queryKey: ["transactions", "deleted"] });
      queryClient.invalidateQueries({ queryKey: ["trips"] });
      toast.success("Transaction restored");
    },
  });
}
