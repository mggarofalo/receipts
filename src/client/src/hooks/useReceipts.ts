import { useMemo } from "react";
import { useStableQuery } from "@/hooks/useStableQuery";
import { useQuery, useMutation, useQueryClient } from "@tanstack/react-query";
import client from "@/lib/api-client";
import { toast } from "sonner";

export function useReceipts(
  offset = 0,
  limit = 50,
  sortBy?: string | null,
  sortDirection?: string | null,
  accountId?: string | null,
  cardId?: string | null,
  q?: string | null,
) {
  const trimmedQ = q?.trim() || undefined;
  const query = useQuery({
    queryKey: ["receipts", "list", offset, limit, sortBy, sortDirection, accountId, cardId, trimmedQ],
    queryFn: async () => {
      const { data, error } = await client.GET("/api/receipts", {
        params: {
          query: {
            offset,
            limit,
            sortBy: sortBy ?? undefined,
            sortDirection: (sortDirection ?? undefined) as "asc" | "desc" | undefined,
            accountId: accountId ?? undefined,
            cardId: cardId ?? undefined,
            q: trimmedQ,
          },
        },
      });
      if (error) throw error;
      return data;
    },
  });
  const base = useStableQuery(query);
  return useMemo(() => ({ ...base, data: query.data?.data, total: Number(query.data?.total ?? 0) }), [base, query.data]);
}

export function useReceipt(id: string | null) {
  return useQuery({
    queryKey: ["receipts", id],
    enabled: !!id,
    queryFn: async () => {
      const { data, error } = await client.GET("/api/receipts/{id}", {
        params: { path: { id: id! } },
      });
      if (error) throw error;
      return data;
    },
  });
}

export function useCreateReceipt() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async (body: {
      description?: string | null;
      location: string;
      date: string;
      taxAmount: number;
    }) => {
      const { data, error } = await client.POST("/api/receipts", { body });
      if (error) throw error;
      return data;
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["receipts"] });
      toast.success("Receipt created");
    },
    onError: () => {
      toast.error("Failed to create receipt");
    },
  });
}

export function useUpdateReceipt() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async (body: {
      id: string;
      description?: string | null;
      location: string;
      date: string;
      taxAmount: number;
    }) => {
      const { error } = await client.PUT("/api/receipts/{id}", {
        params: { path: { id: body.id } },
        body,
      });
      if (error) throw error;
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["receipts"] });
      queryClient.invalidateQueries({ queryKey: ["trips"] });
      toast.success("Receipt updated");
    },
    onError: () => {
      toast.error("Failed to update receipt");
    },
  });
}

export function useDeleteReceipts() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async (ids: string[]) => {
      const { error } = await client.DELETE("/api/receipts", { body: ids });
      if (error) throw error;
    },
    onMutate: async (ids) => {
      await queryClient.cancelQueries({ queryKey: ["receipts"] });
      const previous = queryClient.getQueriesData<{ data: { id: string }[]; total: number }>({ queryKey: ["receipts", "list"] });
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
      toast.error("Failed to delete receipt(s)");
    },
    onSettled: () => {
      queryClient.invalidateQueries({ queryKey: ["receipts"] });
      queryClient.invalidateQueries({ queryKey: ["receipts", "deleted"] });
    },
    onSuccess: () => {
      toast.success("Receipt(s) deleted");
    },
  });
}

export function useDeletedReceipts(offset = 0, limit = 50, sortBy?: string | null, sortDirection?: string | null) {
  const query = useQuery({
    queryKey: ["receipts", "deleted", offset, limit, sortBy, sortDirection],
    queryFn: async () => {
      const { data, error } = await client.GET("/api/receipts/deleted", {
        params: { query: { offset, limit, sortBy: sortBy ?? undefined, sortDirection: (sortDirection ?? undefined) as "asc" | "desc" | undefined } },
      });
      if (error) throw error;
      return data;
    },
  });
  const base = useStableQuery(query);
  return useMemo(() => ({ ...base, data: query.data?.data, total: Number(query.data?.total ?? 0) }), [base, query.data]);
}

export function useCreateCompleteReceipt() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async (body: {
      receipt: { location: string; date: string; taxAmount: number };
      transactions: { amount: number; date: string; accountId: string; cardId: string }[];
      items: {
        receiptItemCode: string;
        description: string;
        quantity: number;
        unitPrice: number;
        category: string;
        subcategory: string;
      }[];
    }) => {
      const { data, error } = await client.POST("/api/receipts/complete", { body });
      if (error) throw error;
      return data;
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["receipts"] });
      queryClient.invalidateQueries({ queryKey: ["transactions"] });
      queryClient.invalidateQueries({ queryKey: ["receipt-items"] });
    },
  });
}

export function useLocationSuggestions(query: string) {
  return useQuery({
    queryKey: ["receipts", "locations", query],
    queryFn: async () => {
      const { data, error } = await client.GET("/api/receipts/locations", {
        params: { query: { q: query || undefined, limit: 20 } },
      });
      if (error) throw error;
      return data?.locations ?? [];
    },
    staleTime: 5 * 60 * 1000,
  });
}

export function useRestoreReceipt() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async (id: string) => {
      const { error } = await client.POST("/api/receipts/{id}/restore", {
        params: { path: { id } },
      });
      if (error) throw error;
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["receipts"] });
      queryClient.invalidateQueries({ queryKey: ["receipts", "deleted"] });
      toast.success("Receipt restored");
    },
    onError: () => {
      toast.error("Failed to restore receipt");
    },
  });
}
